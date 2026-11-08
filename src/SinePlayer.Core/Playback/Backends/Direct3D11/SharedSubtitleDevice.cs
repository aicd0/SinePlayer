// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// The one Direct3D 11 device every subtitle surface draws through.
/// <para>
/// A surface with a device of its own costs a driver instance per chain — its own command queue and its own copy of
/// the driver's state — for nothing: a device is meant to be shared, and this one is already multithread protected
/// (see <see cref="D3D11Device"/>). What stays per surface is the device Win2D draws through, so no two threads ever
/// drive one drawing device.
/// </para>
/// <para>
/// The device is created when the first surface asks for it and released when the last one lets go: a host that never
/// shows subtitles never pays for it, and one that loses its device gets a fresh one with the next surface.
/// </para>
/// </summary>
internal static class SharedSubtitleDevice
{
    private static readonly Lock s_lock = new();

    private static D3D11Device? s_device;
    private static int s_shares;

    /// <summary>Takes a share of the device, creating it if this is the first one asked for.</summary>
    internal static OperationResult<D3D11Device> Rent()
    {
        lock (s_lock)
        {
            if (s_device is null)
            {
                OperationResult<D3D11Device> created = D3D11Device.Create();
                if (!created.IsSuccessful)
                {
                    return created;
                }

                s_device = created.Result;
            }

            s_shares++;
            return new OperationResultCreator<D3D11Device>().Success(s_device);
        }
    }

    /// <summary>Gives a share back, releasing the device when it was the last one.</summary>
    internal static void Return()
    {
        lock (s_lock)
        {
            if (s_shares == 0)
            {
                return;
            }

            s_shares--;

            if (s_shares == 0)
            {
                s_device?.Dispose();
                s_device = null;
            }
        }
    }
}
