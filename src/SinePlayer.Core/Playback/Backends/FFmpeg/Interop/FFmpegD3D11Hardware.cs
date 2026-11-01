// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;

namespace SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

internal sealed partial class FFmpegD3D11Hardware : IFFmpegHardwareBackend
{
    private const int DeviceTypeD3D11Va = 7;
    private const string PixelFormatName = "d3d11";
    private const int PixelFormatNone = -1;

    /// <summary>
    /// <c>offsetof(AVCodecContext, hw_device_ctx)</c> in the headers the payload was built from: where the decoder is
    /// told which device to decode on. Not part of the declared layout — everything between <c>get_format</c> at 192 and
    /// it is bitfields and function pointers — so <see cref="TryWriteHardwareDevice"/> checks before it writes.
    /// </summary>
    private const int HardwareDeviceContextOffset = 560;

    /// <summary>
    /// The device each open decoder was given the pixel format of, by codec context. <c>get_format</c> is handed the
    /// codec context and nothing else, and a second decoder — another tab, another pipeline — needs its own answer, so
    /// this cannot be a single shared value.
    /// </summary>
    private static readonly ConcurrentDictionary<nint, int> sPixelFormats = [];

    internal static OperationResult<IFFmpegHardwareBackend> TryCreate(ID3D11GpuDevice? device)
    {
        OperationResultCreator<IFFmpegHardwareBackend> result = new();

        int pixelFormat = FFmpegNative.av_get_pix_fmt(PixelFormatName);
        if (pixelFormat == PixelFormatNone)
        {
            return result.Failure($"This FFmpeg build has no {PixelFormatName} pixel format.");
        }

        return result.Success(new FFmpegD3D11Hardware(device, pixelFormat));
    }

    private static OperationResult<(nint Reference, nint LockHandle)> TryBuildDeviceContext(ID3D11GpuDevice gpuDevice)
    {
        OperationResultCreator<(nint Reference, nint LockHandle)> result = new();

        nint reference = FFmpegNative.av_hwdevice_ctx_alloc(DeviceTypeD3D11Va);
        if (reference == 0)
        {
            return result.Failure("Unable to allocate an FFmpeg hardware device context.");
        }

        AVBufferRef buffer = Marshal.PtrToStructure<AVBufferRef>(reference);
        AVHWDeviceContext hardwareDevice = Marshal.PtrToStructure<AVHWDeviceContext>(buffer.Data);

        nint device = gpuDevice.NativeDevice;
        Marshal.AddRef(device);
        Marshal.WriteIntPtr(hardwareDevice.HardwareContext, (int)Marshal.OffsetOf<AVD3D11VADeviceContext>(nameof(AVD3D11VADeviceContext.Device)), device);
        nint lockHandle = GCHandle.ToIntPtr(GCHandle.Alloc(gpuDevice.DeviceLock));
        Marshal.WriteIntPtr(hardwareDevice.HardwareContext, (int)Marshal.OffsetOf<AVD3D11VADeviceContext>(nameof(AVD3D11VADeviceContext.Lock)), GetLockPointer());
        Marshal.WriteIntPtr(hardwareDevice.HardwareContext, (int)Marshal.OffsetOf<AVD3D11VADeviceContext>(nameof(AVD3D11VADeviceContext.Unlock)), GetUnlockPointer());
        Marshal.WriteIntPtr(hardwareDevice.HardwareContext, (int)Marshal.OffsetOf<AVD3D11VADeviceContext>(nameof(AVD3D11VADeviceContext.LockContext)), lockHandle);

        // Everything else the context needs is derived from the device when it is initialised.
        int initialised = FFmpegNative.av_hwdevice_ctx_init(reference);
        if (initialised < 0)
        {
            FFmpegNative.av_buffer_unref(ref reference);
            FreeLockHandle(lockHandle);
            return result.Failure($"Unable to use the supplied Direct3D 11 device: {FFmpegInterop.GetErrorMessage(initialised)}");
        }

        return result.Success((reference, lockHandle));
    }

    private static void FreeLockHandle(nint lockHandle)
    {
        if (lockHandle != 0)
        {
            GCHandle.FromIntPtr(lockHandle).Free();
        }
    }

    private static nint GetLockPointer()
    {
        unsafe
        {
            return (nint)(delegate* unmanaged[Cdecl]<nint, void>)&EnterDevice;
        }
    }

    private static nint GetUnlockPointer()
    {
        unsafe
        {
            return (nint)(delegate* unmanaged[Cdecl]<nint, void>)&ExitDevice;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void EnterDevice(nint lockHandle)
    {
        if (GCHandle.FromIntPtr(lockHandle).Target is object deviceLock)
        {
            Monitor.Enter(deviceLock);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ExitDevice(nint lockHandle)
    {
        if (GCHandle.FromIntPtr(lockHandle).Target is object deviceLock)
        {
            Monitor.Exit(deviceLock);
        }
    }

    private static nint GetFormatPointer()
    {
        unsafe
        {
            return (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&SelectPixelFormat;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SelectPixelFormat(nint context, nint formats)
    {
        int hardware = sPixelFormats.TryGetValue(context, out int format) ? format : PixelFormatNone;
        unsafe
        {
            for (int* offered = (int*)formats; *offered != PixelFormatNone; offered++)
            {
                if (*offered == hardware)
                {
                    return hardware;
                }
            }

            return *(int*)formats;
        }
    }

    /// <summary>
    /// Points the decoder at the device, having first checked that the field it writes is the one it thinks: a context
    /// fresh from the allocator has no device, so anything other than nothing at that offset means the layout is not the
    /// one this was built against — and writing there would put a pointer into an unrelated field of a live structure.
    /// <para>
    /// The offset cannot be derived from the binding, and this is the only place that names it. It is
    /// <c>offsetof(AVCodecContext, hw_device_ctx)</c> as the shipped headers give it (560, alongside
    /// <c>get_format</c> at 192); the field is a public member that FFmpeg expects the caller to set, and the check is
    /// what makes a rebuild of the payload a loud failure instead of a corrupted decoder.
    /// </para>
    /// </summary>
    private static OperationResult TryWriteHardwareDevice(nint codecContext, nint reference)
    {
        nint installed = Marshal.ReadIntPtr(codecContext, HardwareDeviceContextOffset);
        if (installed != 0)
        {
            return OperationResult.Failure($"The codec context already holds {installed:X} where hw_device_ctx is expected, so this FFmpeg build does not lay the context out as byte {HardwareDeviceContextOffset} suggests.");
        }

        Marshal.WriteIntPtr(codecContext, HardwareDeviceContextOffset, reference);

        AVBufferRef written = Marshal.PtrToStructure<AVBufferRef>(Marshal.ReadIntPtr(codecContext, HardwareDeviceContextOffset));
        AVBufferRef expected = Marshal.PtrToStructure<AVBufferRef>(reference);
        if (written.Buffer != expected.Buffer || written.Data != expected.Data)
        {
            // The pointer is installed and the context now owns it, so it is not released here: freeing it as well
            // would free the device twice.
            return OperationResult.Failure($"Unable to hand the device to the decoder: what it reads back is not the reference that was written.");
        }

        return OperationResult.Success();
    }

    private readonly ID3D11GpuDevice? _device;
    private readonly int _pixelFormat;
    private nint _lockHandle;
    private nint _codecContext;
    private bool _disposed;

    private FFmpegD3D11Hardware(ID3D11GpuDevice? device, int pixelFormat)
    {
        _device = device;
        _pixelFormat = pixelFormat;
    }

    public int FramePixelFormat => _pixelFormat;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach();
    }

    public PixelFormat GetFrameLayout(PixelFormat streamFormat)
    {
        return streamFormat switch
        {
            PixelFormat.Yuv420P or PixelFormat.Nv12 => PixelFormat.Nv12,
            PixelFormat.Yuv420P10 or PixelFormat.P010 => PixelFormat.P010,
            _ => streamFormat,
        };
    }

    public OperationResult TryAttach(nint codecContext)
    {
        // A previous attachment belongs to a context that is already gone, and its lock handle would otherwise be left
        // holding the renderer's lock object alive for the rest of the process.
        Detach();

        nint reference;
        if (_device is null)
        {
            reference = 0;
            int created = FFmpegNative.av_hwdevice_ctx_create(ref reference, DeviceTypeD3D11Va, 0, 0, 0);
            if (created < 0)
            {
                return OperationResult.Failure($"No Direct3D 11 device to decode with: {FFmpegInterop.GetErrorMessage(created)}");
            }
        }
        else
        {
            OperationResult<(nint Reference, nint LockHandle)> built = TryBuildDeviceContext(_device);
            if (!built.IsSuccessful)
            {
                return OperationResult.Failure(built);
            }

            (reference, _lockHandle) = built.Result;
        }

        Marshal.WriteIntPtr(codecContext, (int)Marshal.OffsetOf<AVCodecContext>(nameof(AVCodecContext.GetFormat)), GetFormatPointer());
        OperationResult written = TryWriteHardwareDevice(codecContext, reference);
        if (!written.IsSuccessful)
        {
            FFmpegNative.av_buffer_unref(ref reference);
            FreeLockHandle(_lockHandle);
            _lockHandle = 0;
            return written;
        }

        _codecContext = codecContext;
        sPixelFormats[codecContext] = _pixelFormat;
        return OperationResult.Success();
    }

    public void Detach()
    {
        nint codecContext = _codecContext;
        _codecContext = 0;
        if (codecContext != 0)
        {
            sPixelFormats.TryRemove(codecContext, out _);
        }

        nint lockHandle = _lockHandle;
        _lockHandle = 0;
        FreeLockHandle(lockHandle);
    }
}
