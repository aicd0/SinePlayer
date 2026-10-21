// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// Owns a Direct3D 11 device and its immediate context. Every GPU resource the backend creates comes
/// from this device, which is what allows the decoder, the filters and the renderer to share textures
/// without a copy.
/// <para>
/// The interop is generated from the Windows metadata by CsWin32, the same tool the application already
/// uses for its Win32 calls, so no COM vtable is hand written. Calls go through runtime callable
/// wrappers; if profiling ever shows that cost matters, switching CsWin32 to its unmanaged mode is a
/// change contained to this backend.
/// </para>
/// </summary>
internal sealed class D3D11Device : IDisposable
{
    /// <summary><c>D3D11_SDK_VERSION</c>.</summary>
    private const uint SdkVersion = 7;

    private static readonly D3D_FEATURE_LEVEL[] sFeatureLevels =
    [
        D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
    ];

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private nint _nativeDevice;
    private bool _disposed;

    private D3D11Device(ID3D11Device device, ID3D11DeviceContext context, D3D_FEATURE_LEVEL featureLevel)
    {
        _device = device;
        _context = context;
        _nativeDevice = Marshal.GetComInterfaceForObject(device, typeof(ID3D11Device));
        FeatureLevel = featureLevel;
    }

    internal ID3D11Device Device => _device ?? throw new ObjectDisposedException(nameof(D3D11Device));

    internal ID3D11DeviceContext Context => _context ?? throw new ObjectDisposedException(nameof(D3D11Device));

    /// <summary>
    /// The device as the native pointer a decoder can be handed, valid for as long as this object lives.
    /// It carries a reference of its own so that it stays valid even after the interfaces above are gone.
    /// </summary>
    internal nint NativeDevice => _nativeDevice;

    internal D3D_FEATURE_LEVEL FeatureLevel { get; }

    /// <summary>
    /// Creates a hardware device with the capabilities video rendering needs. BGRA support is what
    /// lets decoded frames be presented directly.
    /// </summary>
    internal static bool TryCreate(out D3D11Device? device, out string error)
    {
        device = null;

        HRESULT result = PInvoke.D3D11CreateDevice(
            null,
            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            default,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            sFeatureLevels,
            SdkVersion,
            out ID3D11Device? createdDevice,
            out D3D_FEATURE_LEVEL featureLevel,
            out ID3D11DeviceContext? createdContext);

        if (result.Value < 0 || createdDevice is null || createdContext is null)
        {
            D3D11Interop.Release(createdContext);
            D3D11Interop.Release(createdDevice);
            error = $"Unable to create a Direct3D 11 device: {result}";
            return false;
        }

        device = new D3D11Device(createdDevice, createdContext, featureLevel);
        error = string.Empty;
        return true;
    }

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

    private static void Release(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        if (Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }
}
