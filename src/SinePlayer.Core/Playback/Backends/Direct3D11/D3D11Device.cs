// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Utils;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

internal sealed partial class D3D11Device : IDisposable
{
    private const string TAG = nameof(D3D11Device);

    /// <summary><c>D3D11_SDK_VERSION</c>.</summary>
    private const uint SdkVersion = 7;

    /// <summary>
    /// <c>IID_ID3D11Multithread</c>. The interface is declared in <c>d3d11_4.h</c> and is not bound by the projection, so
    /// it is reached through its vtable: three <c>IUnknown</c> slots, then <c>Enter</c>, <c>Leave</c>, and the two
    /// protection calls.
    /// </summary>
    private static readonly Guid sMultithreadId = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    /// <summary><c>ID3D11Multithread::SetMultithreadProtected</c> in the vtable.</summary>
    private const int SetMultithreadProtectedSlot = 5;

    private static readonly D3D_FEATURE_LEVEL[] sFeatureLevels =
    [
        D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
    ];

    private static readonly D3D_DRIVER_TYPE[] sDriverTypes =
    [
        D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
        D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP,
    ];

    internal static OperationResult<D3D11Device> Create()
    {
        OperationResultCreator<D3D11Device> result = new();
        string refused = string.Empty;

        foreach (D3D_DRIVER_TYPE driverType in sDriverTypes)
        {
            OperationResult<D3D11Device> created = Create(driverType);
            if (!created.IsSuccessful)
            {
                refused += refused.Length == 0 ? $"{driverType}: {created.Message}" : $"; {driverType}: {created.Message}";
                continue;
            }

            if (created.Result.IsSoftware)
            {
                PlaybackLogger.W(TAG, $"No accelerated Direct3D 11 device ({refused}); rendering in software.");
            }

            return created;
        }

        return result.Failure($"Unable to create a Direct3D 11 device. {refused}");
    }

    internal static OperationResult<D3D11Device> Create(D3D_DRIVER_TYPE driverType)
    {
        OperationResultCreator<D3D11Device> result = new();

        // Video support is asked for on the accelerated path only: it makes creation fail outright when the driver is
        // older than WDDM 1.2, which is a fault that should be reported rather than found later as a decode error.
        // The software rasterizer has no video hardware at all, so demanding it there would only lose the fallback.
        D3D11_CREATE_DEVICE_FLAG flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        if (driverType == D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE)
        {
            flags |= D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        }

        HRESULT created = PInvoke.D3D11CreateDevice(
            null,
            driverType,
            default,
            flags,
            sFeatureLevels,
            SdkVersion,
            out ID3D11Device? createdDevice,
            out D3D_FEATURE_LEVEL featureLevel,
            out ID3D11DeviceContext? createdContext);

        if (created.Value < 0 || createdDevice is null || createdContext is null)
        {
            D3D11Interop.Release(createdContext);
            D3D11Interop.Release(createdDevice);
            return result.Failure($"Unable to create a Direct3D 11 device: {created}");
        }

        return result.Success(new D3D11Device(createdDevice, createdContext, featureLevel, driverType));
    }

    /// <summary>
    /// Makes the immediate context safe to call from more than one thread, which Direct3D does not do by itself: the
    /// device is thread-safe, the context is not, and the documented answer to sharing one is a synchronization
    /// mechanism — this one, provided by the runtime.
    /// <para>
    /// It matters here because one device is deliberately shared: the video renderer draws through the immediate context
    /// while libavcodec submits and tears down hardware decoding on the same context, on other threads. Handing that
    /// context to the driver from two threads at once is what it must not do, and the driver's own multi-threading
    /// support is the supported way of telling it to expect it. Serializing by hand cannot stand in for it: a lock the
    /// application takes around its own calls says nothing to the decoder, and one it hands the decoder covers only the
    /// calls libavcodec knows it needs (see <c>AVD3D11VADeviceContext::lock</c>).
    /// </para>
    /// </summary>
    private static void EnableMultithreadProtection(nint device)
    {
        Guid iid = sMultithreadId;
        int queried = Marshal.QueryInterface(device, in iid, out nint multithread);
        if (queried < 0)
        {
            PlaybackLogger.W(TAG, $"This Direct3D 11 device offers no multithread protection (0x{queried:X8}), so the decoder and the renderer share its context without the driver knowing.");
            return;
        }

        try
        {
            unsafe
            {
                nint* vtable = *(nint**)multithread;
                var setProtected = (delegate* unmanaged[Stdcall]<nint, int, int>)vtable[SetMultithreadProtectedSlot];

                // The answer says whether protection was already on, not whether it worked; either way it is on now.
                setProtected(multithread, 1);
            }
        }
        finally
        {
            Marshal.Release(multithread);
        }
    }

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private nint _nativeDevice;
    private bool _disposed;

    private D3D11Device(ID3D11Device device, ID3D11DeviceContext context, D3D_FEATURE_LEVEL featureLevel, D3D_DRIVER_TYPE driverType)
    {
        _device = device;
        _context = context;
        _nativeDevice = Marshal.GetComInterfaceForObject(device, typeof(ID3D11Device));
        FeatureLevel = featureLevel;
        DriverType = driverType;

        EnableMultithreadProtection(_nativeDevice);
    }

    internal ID3D11Device Device => _device ?? throw new ObjectDisposedException(nameof(D3D11Device));

    internal ID3D11DeviceContext Context => _context ?? throw new ObjectDisposedException(nameof(D3D11Device));

    internal nint NativeDevice => _nativeDevice;

    internal D3D_FEATURE_LEVEL FeatureLevel { get; }

    internal D3D_DRIVER_TYPE DriverType { get; }

    internal bool IsSoftware => DriverType == D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        nint nativeDevice = _nativeDevice;
        _nativeDevice = 0;
        if (nativeDevice != 0)
        {
            Marshal.Release(nativeDevice);
        }

        D3D11Interop.Release(_context);
        _context = null;
        D3D11Interop.Release(_device);
        _device = null;
    }
}
