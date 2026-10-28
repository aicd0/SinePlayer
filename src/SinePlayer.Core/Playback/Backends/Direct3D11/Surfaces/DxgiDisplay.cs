// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>What the display the picture is meant for can do, as the system reports it.</summary>
internal readonly record struct DisplayHdrState(bool IsHdrComposed, float PeakLuminanceNits, float MaxFullFrameLuminanceNits);

/// <summary>
/// Asks the system what the display is composed for.
/// <para>
/// The answer that matters is not "is this an HDR monitor" but "is the desktop being composed for HDR", because that is
/// what decides how a swap chain's contents are interpreted: a capable monitor with HDR switched off is an SDR display
/// in every way that concerns us. DXGI reports it as the output's colour space, which is PQ/BT.2020 exactly when the
/// desktop is composing for HDR.
/// </para>
/// <para>
/// Which output is asked is the first one attached to the desktop, preferring the primary. That is right for a window on
/// the primary display and wrong for a window dragged to another one; the window handle never reaches the engine, so
/// there is nothing better to ask until the presentation target can say which display it is on.
/// </para>
/// </summary>
internal static class DxgiDisplay
{
    internal static OperationResult<DisplayHdrState> TryQuery()
    {
        OperationResultCreator<DisplayHdrState> result = new();

        try
        {
            Guid factoryId = typeof(IDXGIFactory2).GUID;
            HRESULT factoryResult = PInvoke.CreateDXGIFactory2(default, factoryId, out object factoryObject);
            if (factoryResult.Value < 0 || factoryObject is not IDXGIFactory1 factory)
            {
                return result.Failure($"Unable to create a DXGI factory: {factoryResult}");
            }

            HRESULT adapterResult = factory.EnumAdapters1(0, out IDXGIAdapter1 adapter);
            if (adapterResult.Value < 0)
            {
                return result.Failure($"Unable to reach the graphics adapter: {adapterResult}");
            }

            try
            {
                return TryReadOutput(adapter, result);
            }
            finally
            {
                D3D11Interop.Release(adapter);
            }
        }
        catch (Exception exception)
        {
            return result.Failure($"Unable to read what the display supports: {exception.Message}");
        }
    }

    private static unsafe OperationResult<DisplayHdrState> TryReadOutput(IDXGIAdapter1 adapter, OperationResultCreator<DisplayHdrState> result)
    {
        DisplayHdrState found = default;
        bool haveFound = false;

        for (uint index = 0; ; index++)
        {
            HRESULT outputResult = adapter.EnumOutputs(index, out IDXGIOutput output);
            if (outputResult.Value < 0)
            {
                break;
            }

            try
            {
                if (output is not IDXGIOutput6 output6)
                {
                    continue;
                }

                DXGI_OUTPUT_DESC1 description = output6.GetDesc1();
                if (!description.AttachedToDesktop)
                {
                    continue;
                }

                var state = new DisplayHdrState(
                    description.ColorSpace == DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020,
                    description.MaxLuminance,
                    description.MaxFullFrameLuminance);

                // The primary output is the one at the desktop's origin, and asking about it is the best guess when
                // nothing tells us which display the picture is on.
                bool isPrimary = description.DesktopCoordinates.left == 0 && description.DesktopCoordinates.top == 0;
                if (!haveFound || isPrimary)
                {
                    found = state;
                    haveFound = true;
                }

                if (isPrimary)
                {
                    break;
                }
            }
            finally
            {
                D3D11Interop.Release(output);
            }
        }

        return haveFound
            ? result.Success(found)
            : result.Failure("No display attached to the desktop answered what it can show.");
    }
}
