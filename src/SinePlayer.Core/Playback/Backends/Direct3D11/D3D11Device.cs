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

    internal static OperationResult<D3D11Device> Create(D3D_DRIVER_TYPE driverType)
    {
        OperationResultCreator<D3D11Device> result = new();

        HRESULT created = PInvoke.D3D11CreateDevice(
            null,
            driverType,
            default,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
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
    }

    internal ID3D11Device Device => _device ?? throw new ObjectDisposedException(nameof(D3D11Device));

    internal ID3D11DeviceContext Context => _context ?? throw new ObjectDisposedException(nameof(D3D11Device));

    internal nint NativeDevice => _nativeDevice;

    internal D3D_FEATURE_LEVEL FeatureLevel { get; }

    internal D3D_DRIVER_TYPE DriverType { get; }

    internal bool IsSoftware => DriverType == D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP;

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
