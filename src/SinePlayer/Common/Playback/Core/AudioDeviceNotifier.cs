// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Runtime.InteropServices;
using System.Threading;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Playback.Backends.Wasapi;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace SinePlayer.Common.Playback.Core;

internal sealed class AudioDeviceNotifier
{
    private const string TAG = nameof(AudioDeviceNotifier);

    private const int CoalesceMilliseconds = 300;

    private static readonly Lock sSharedLock = new();
    private static AudioDeviceNotifier? sShared;

    public static AudioDeviceNotifier Shared
    {
        get
        {
            lock (sSharedLock)
            {
                return sShared ??= new AudioDeviceNotifier();
            }
        }
    }

    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private readonly Sink _sink;
    private Action? _changed;
    private int _subscriberCount;
    private IMMDeviceEnumerator? _enumerator;

    private AudioDeviceNotifier()
    {
        _timer = new Timer(_ => Raise(), null, Timeout.Infinite, Timeout.Infinite);
        _sink = new Sink(this);
    }

    public void Subscribe(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            _changed += handler;
            _subscriberCount++;

            if (_subscriberCount > 1 || _enumerator is not null)
            {
                return;
            }
        }

        Register();
    }

    public void Unsubscribe(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            if (_subscriberCount == 0)
            {
                return;
            }

            _changed -= handler;
            _subscriberCount--;

            if (_subscriberCount > 0 || _enumerator is null)
            {
                return;
            }
        }

        Unregister();
    }

    private void Register()
    {
        try
        {
            unsafe
            {
                PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
                PInvoke.CoCreateInstance(WasapiAudioEndpoints.MMDeviceEnumeratorClassId, null, CLSCTX.CLSCTX_ALL, out IMMDeviceEnumerator enumerator);

                lock (_lock)
                {
                    _enumerator = enumerator;
                }

                enumerator.RegisterEndpointNotificationCallback(_sink);
            }
        }
        catch (Exception exception)
        {
            // Without the notifications the player still plays; it only stops following the machine.
            Logger.W(TAG, $"Unable to watch the audio output devices: {exception.Message}");
        }
    }

    private void Unregister()
    {
        IMMDeviceEnumerator? enumerator;
        lock (_lock)
        {
            enumerator = _enumerator;
            _enumerator = null;
        }

        if (enumerator is null)
        {
            return;
        }

        try
        {
            enumerator.UnregisterEndpointNotificationCallback(_sink);
        }
        catch (Exception exception)
        {
            Logger.W(TAG, $"Unable to stop watching the audio output devices: {exception.Message}");
        }

        Marshal.ReleaseComObject(enumerator);
    }

    /// <summary>Restarts the wait, so that only a pause in the notifications lets the event through.</summary>
    private void Schedule()
    {
        lock (_lock)
        {
            if (_subscriberCount == 0)
            {
                return;
            }
        }

        try
        {
            _timer.Change(CoalesceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // The timer lives as long as the process, so this cannot happen; it is here so that a callback arriving
            // during shutdown has somewhere harmless to land.
        }
    }

    private void Raise()
    {
        Action? changed;
        lock (_lock)
        {
            changed = _subscriberCount > 0 ? _changed : null;
        }

        if (changed is null)
        {
            return;
        }

        // One listener that fails is not a reason to leave the others uninformed.
        foreach (Action handler in changed.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                Logger.W(TAG, $"A handler of the audio output change failed: {exception.Message}");
            }
        }
    }

    private sealed class Sink(AudioDeviceNotifier owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(PCWSTR pwstrDeviceId, DEVICE_STATE dwNewState)
        {
            owner.Schedule();
        }

        public void OnDeviceAdded(PCWSTR pwstrDeviceId)
        {
            owner.Schedule();
        }

        public void OnDeviceRemoved(PCWSTR pwstrDeviceId)
        {
            owner.Schedule();
        }

        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR pwstrDefaultDeviceId)
        {
            // Only the device sound is played through matters here.
            if (flow != EDataFlow.eRender)
            {
                return;
            }

            owner.Schedule();
        }

        public void OnPropertyValueChanged(PCWSTR pwstrDeviceId, PROPERTYKEY key)
        {
            owner.Schedule();
        }
    }
}
