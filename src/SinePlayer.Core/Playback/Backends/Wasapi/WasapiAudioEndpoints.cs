// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace SinePlayer.Core.Playback.Backends.Wasapi;

/// <summary>An output device sound can be played through.</summary>
/// <param name="Id">The endpoint id, which is what opening this device takes.</param>
/// <param name="Name">The device's own name, for a person to choose from.</param>
/// <param name="IsDefault">Whether this is the device Windows would pick on its own.</param>
public sealed record WasapiAudioEndpoint(string Id, string Name, bool IsDefault);

/// <summary>
/// The output devices this machine offers. Enumeration lives here rather than in the pipeline because devices
/// are a backend's business: the pipeline deals in factories, and a factory that names an endpoint is how a
/// caller picks one.
/// </summary>
public static class WasapiAudioEndpoints
{
    /// <summary>The class id of the endpoint enumerator, which is not in the Win32 metadata.</summary>
    internal static readonly Guid MMDeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>
    /// Every active output device, with the default first. A machine with no output device answers an empty
    /// list rather than an error, because "nothing to play through" is a fact about the machine rather than a
    /// failure of the question.
    /// </summary>
    public static OperationResult<IReadOnlyList<WasapiAudioEndpoint>> TryGetRenderEndpoints()
    {
        OperationResultCreator<IReadOnlyList<WasapiAudioEndpoint>> result = new();

        try
        {
            // The engine creates its objects on threads of its own, and a caller may reach this from any of
            // them. An apartment the host already chose is left alone.
            unsafe
            {
                PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            }

            PInvoke.CoCreateInstance(MMDeviceEnumeratorClassId, null, CLSCTX.CLSCTX_ALL, out IMMDeviceEnumerator enumerator);

            // A machine can have no default device and still have devices, so a missing default is not a
            // failure of the enumeration.
            string? defaultId = null;
            try
            {
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out IMMDevice defaultDevice);
                defaultId = ReadId(defaultDevice);
            }
            catch (Exception)
            {
                defaultId = null;
            }

            enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE.DEVICE_STATE_ACTIVE, out IMMDeviceCollection collection);
            collection.GetCount(out uint count);

            List<WasapiAudioEndpoint> found = [];
            for (uint index = 0; index < count; index++)
            {
                try
                {
                    collection.Item(index, out IMMDevice device);
                    string? id = ReadId(device);
                    if (id is null)
                    {
                        continue;
                    }

                    found.Add(new WasapiAudioEndpoint(id, ReadName(device) ?? id, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception)
                {
                    // One device that cannot be read says nothing about the others, and a list that is one
                    // shorter is better than a list that cannot be shown at all.
                }
            }

            // The default device goes first: it is what a caller gets when it names nothing, so a list that
            // offers a choice should start with it. The rest keep the order Windows gave them.
            WasapiAudioEndpoint? defaultEndpoint = found.Find(endpoint => endpoint.IsDefault);
            if (defaultEndpoint is not null)
            {
                found.Remove(defaultEndpoint);
                found.Insert(0, defaultEndpoint);
            }

            return result.Success(found);
        }
        catch (Exception exception)
        {
            return result.Failure($"Unable to list the audio output devices: {exception.Message}");
        }
    }

    private static unsafe string? ReadId(IMMDevice device)
    {
        device.GetId(out PWSTR pointer);
        try
        {
            return pointer.ToString();
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)pointer.Value);
        }
    }

    /// <summary>
    /// The device's friendly name, which is what a person recognises it by. A device whose property store
    /// refuses is still usable — it just has no name to show — so this answers null rather than failing.
    /// </summary>
    private static unsafe string? ReadName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(STGM.STGM_READ, out IPropertyStore store);

            PROPVARIANT value = default;
            store.GetValue(PInvoke.PKEY_Device_FriendlyName, out value);
            try
            {
                PInvoke.PropVariantToStringAlloc(value, out PWSTR name);
                try
                {
                    return name.ToString();
                }
                finally
                {
                    Marshal.FreeCoTaskMem((nint)name.Value);
                }
            }
            finally
            {
                PInvoke.PropVariantClear(ref value);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
