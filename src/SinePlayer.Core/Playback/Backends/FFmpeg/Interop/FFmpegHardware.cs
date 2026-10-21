// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

/// <summary>
/// Hardware decoding through D3D11VA: the device libavcodec decodes on, and the answer it expects when
/// it asks which pixel format the decoder should produce. Every value that only makes sense to FFmpeg's
/// hardware vocabulary is kept here, so the decoder above only has to ask whether hardware is in use.
/// <para>
/// The device belongs to FFmpeg rather than to the renderer: frames are copied back into system memory
/// before they leave the decoder, so nothing on this layer knows a renderer exists.
/// </para>
/// </summary>
internal static class FFmpegHardware
{
    /// <summary><c>AV_HWDEVICE_TYPE_D3D11VA</c>.</summary>
    private const int DeviceTypeD3D11Va = 7;

    /// <summary><c>AV_PIX_FMT_NONE</c>, which is also how a <c>get_format</c> callback refuses every offer.</summary>
    private const int PixelFormatNone = -1;

    /// <summary>
    /// <c>offsetof(AVCodecContext, hw_device_ctx)</c> in the pinned build, taken with the compiler
    /// against its headers. The field sits far past the part of the context declared in
    /// <see cref="AVCodecContext"/>, so it is addressed by offset. That stays honest because FFmpeg only
    /// moves fields when it bumps the library major version, and <see cref="FFmpegRuntime"/> refuses to
    /// start with a build whose major version it was not written against.
    /// </summary>
    private const int DeviceContextOffset = 560;

    /// <summary>
    /// <c>offsetof(AVCodecContext, hw_frames_ctx)</c> in the pinned build: the frames context libavcodec
    /// allocated for this decoder. The offset before <see cref="DeviceContextOffset"/> for the same reason.
    /// </summary>
    private const int FramesContextOffset = 552;

    private static int sPixelFormatD3D11 = PixelFormatNone;

    /// <summary>The pixel format libavcodec produces hardware frames in, or none when it never engaged.</summary>
    internal static int PixelFormatD3D11 => sPixelFormatD3D11;

    /// <summary>
    /// Creates a D3D11 device and asks a codec context to decode with it, or, when one is supplied, asks it
    /// to decode on that device instead. The context owns the device afterwards, so there is nothing to
    /// release separately.
    /// </summary>
    /// <param name="suppliedDevice">
    /// A device the caller already has, as a borrowed native pointer, or zero to create one. Decoding on the
    /// device that draws the frames is what keeps a decoded texture usable without a copy.
    /// </param>
    /// <param name="deviceLock">
    /// The lock that guards that device's immediate context, which libavcodec takes around every use of it.
    /// Required with a supplied device, because the whole point of sharing one is that two threads use it.
    /// </param>
    /// <param name="lockHandle">
    /// The handle that keeps <paramref name="deviceLock"/> alive for as long as libavcodec can call the lock.
    /// Freeing it is the caller's job, and only after the context has been freed.
    /// </param>
    internal static bool TryAttachDevice(nint context, nint suppliedDevice, object? deviceLock, out nint lockHandle, out string error)
    {
        lockHandle = 0;

        int pixelFormat = FFmpegNative.av_get_pix_fmt("d3d11");
        if (pixelFormat == PixelFormatNone)
        {
            error = "This FFmpeg build has no d3d11 pixel format.";
            return false;
        }

        nint reference;
        if (suppliedDevice == 0)
        {
            reference = 0;
            int result = FFmpegNative.av_hwdevice_ctx_create(ref reference, DeviceTypeD3D11Va, 0, 0, 0);
            if (result < 0)
            {
                error = $"No D3D11 device to decode with: {FFmpegInterop.GetErrorMessage(result)}";
                return false;
            }
        }
        else
        {
            if (deviceLock is null)
            {
                error = "A device to decode on has to come with the lock that guards it.";
                return false;
            }

            if (!TryBuildDeviceContext(suppliedDevice, deviceLock, out reference, out lockHandle, out error))
            {
                return false;
            }
        }

        sPixelFormatD3D11 = pixelFormat;

        Marshal.WriteIntPtr(context, DeviceContextOffset, reference);
        Marshal.WriteIntPtr(context, (int)Marshal.OffsetOf<AVCodecContext>(nameof(AVCodecContext.GetFormat)), GetFormatPointer());
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Builds a device context around a device the caller owns. libavcodec takes over the reference it is
    /// given and releases it when the context is freed, so it is given one of its own rather than the
    /// caller's.
    /// </summary>
    private static bool TryBuildDeviceContext(nint device, object deviceLock, out nint reference, out nint lockHandle, out string error)
    {
        lockHandle = 0;
        reference = FFmpegNative.av_hwdevice_ctx_alloc(DeviceTypeD3D11Va);
        if (reference == 0)
        {
            error = "Unable to allocate an FFmpeg hardware device context.";
            return false;
        }

        AVBufferRef buffer = Marshal.PtrToStructure<AVBufferRef>(reference);
        AVHWDeviceContext hardwareDevice = Marshal.PtrToStructure<AVHWDeviceContext>(buffer.Data);

        Marshal.AddRef(device);
        Marshal.WriteIntPtr(hardwareDevice.HardwareContext, (int)Marshal.OffsetOf<AVD3D11VADeviceContext>(nameof(AVD3D11VADeviceContext.Device)), device);
        lockHandle = GCHandle.ToIntPtr(GCHandle.Alloc(deviceLock));
        Marshal.WriteIntPtr(hardwareDevice.HardwareContext, (int)Marshal.OffsetOf<AVD3D11VADeviceContext>(nameof(AVD3D11VADeviceContext.Lock)), GetLockPointer());
        Marshal.WriteIntPtr(hardwareDevice.HardwareContext, (int)Marshal.OffsetOf<AVD3D11VADeviceContext>(nameof(AVD3D11VADeviceContext.Unlock)), GetUnlockPointer());
        Marshal.WriteIntPtr(hardwareDevice.HardwareContext, (int)Marshal.OffsetOf<AVD3D11VADeviceContext>(nameof(AVD3D11VADeviceContext.LockContext)), lockHandle);

        // Everything else the context needs is derived from the device when it is initialised.
        int result = FFmpegNative.av_hwdevice_ctx_init(reference);
        if (result < 0)
        {
            FFmpegNative.av_buffer_unref(ref reference);
            FreeLockHandle(lockHandle);
            lockHandle = 0;
            error = $"Unable to use the supplied D3D11 device: {FFmpegInterop.GetErrorMessage(result)}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Releases the reference that keeps a device lock alive. Call it only once libavcodec is done.</summary>
    internal static void FreeLockHandle(nint lockHandle)
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

    /// <summary>libavcodec taking the device lock before a sequence of calls on the immediate context.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void EnterDevice(nint lockHandle)
    {
        if (GCHandle.FromIntPtr(lockHandle).Target is object deviceLock)
        {
            Monitor.Enter(deviceLock);
        }
    }

    /// <summary>libavcodec giving the device lock back.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ExitDevice(nint lockHandle)
    {
        if (GCHandle.FromIntPtr(lockHandle).Target is object deviceLock)
        {
            Monitor.Exit(deviceLock);
        }
    }

    /// <summary>
    /// The device a codec context decodes on and the immediate context that can read its textures back, or
    /// zeroes when it was opened without one.
    /// </summary>
    internal static (nint Device, nint Context) ReadDevice(nint context)
    {
        nint reference = Marshal.ReadIntPtr(context, DeviceContextOffset);
        if (reference == 0)
        {
            return (0, 0);
        }

        AVBufferRef buffer = Marshal.PtrToStructure<AVBufferRef>(reference);
        AVHWDeviceContext hardwareDevice = Marshal.PtrToStructure<AVHWDeviceContext>(buffer.Data);
        if (hardwareDevice.HardwareContext == 0)
        {
            return (0, 0);
        }

        AVD3D11VADeviceContext hardware = Marshal.PtrToStructure<AVD3D11VADeviceContext>(hardwareDevice.HardwareContext);
        return (hardware.Device, hardware.DeviceContext);
    }

    /// <summary>
    /// The layout of the pixels inside a hardware frame. It is not the frame's own format — that is the
    /// hardware one — and not the decoder's software format either: it is what the frames context says the
    /// texture holds, which is what anything drawing it has to sample it as.
    /// </summary>
    internal static int ReadHardwareFrameFormat(nint context)
    {
        nint reference = Marshal.ReadIntPtr(context, FramesContextOffset);
        if (reference == 0)
        {
            return PixelFormatNone;
        }

        AVBufferRef buffer = Marshal.PtrToStructure<AVBufferRef>(reference);
        AVHWFramesContext frames = Marshal.PtrToStructure<AVHWFramesContext>(buffer.Data);
        return frames.SoftwareFormat;
    }

    private static nint GetFormatPointer()
    {
        unsafe
        {
            return (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&SelectPixelFormat;
        }
    }

    /// <summary>
    /// libavcodec asking which of the pixel formats it can produce should be used. Hardware wins when it
    /// is among the offers; otherwise the first offer is taken, which is the software one, so a machine
    /// without a decoder for this codec and stream still decodes.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SelectPixelFormat(nint context, nint formats)
    {
        int hardware = sPixelFormatD3D11;
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
}
