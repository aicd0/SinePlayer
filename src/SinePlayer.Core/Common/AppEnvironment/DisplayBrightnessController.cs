// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

using SinePlayer.Core.Common.DebugTools;

using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Graphics.Gdi;

namespace SinePlayer.Core.Common.AppEnvironment;

/// <summary>
/// Changes the brightness of the display the player is on.
/// <para>
/// Reaching a display's own control, over DDC/CI or through the panel's WMI interface, is a blocking call that takes
/// tens of milliseconds, while a drag produces a value per pointer move. The level asked for is therefore kept and
/// applied by one background loop, which only ever applies the latest one.
/// </para>
/// <para>
/// The display is the one the player window is on when it starts. A player that is moved to another display keeps
/// adjusting the one it started on until it is opened again.
/// </para>
/// </summary>
public sealed class DisplayBrightnessController
{
    private const string TAG = nameof(DisplayBrightnessController);

    private const int MinimumApplyIntervalMilliseconds = 30;

    private static readonly Lock sSharedLock = new();
    private static DisplayBrightnessController? sShared;

    public static DisplayBrightnessController Shared
    {
        get
        {
            lock (sSharedLock)
            {
                return sShared ??= new DisplayBrightnessController();
            }
        }
    }

    private readonly Lock _lock = new();

    // Read by the gesture path and written by the applying loop, so both sides use the interlocked pair; the level is
    // one int and there is nothing else to keep in step with it. -1 means unknown or not adjustable.
    private int _level = -1;

    private int _requestedLevel = -1;
    private bool _applyLoopActive;
    private int _resolveState;
    private IBackend? _backend;

    private DisplayBrightnessController()
    {
    }

    /// <summary>The level this display is at, 0 to 100, or -1 when it cannot be adjusted.</summary>
    public int Level => Volatile.Read(ref _level);

    /// <summary>
    /// Finds out how this display can be adjusted and what it is at. Safe to call from any thread and to call again:
    /// the work that blocks runs behind the call. A display that answers nothing is asked again on the next call.
    /// </summary>
    public void EnsureResolved()
    {
        if (Interlocked.CompareExchange(ref _resolveState, 1, 0) != 0)
        {
            return;
        }

        // Which display this is has to be asked while the window is still the active one of the calling thread.
        HMONITOR monitor = PInvoke.MonitorFromWindow(PInvoke.GetActiveWindow(), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);

        _ = Task.Run(() => Resolve(monitor));
    }

    /// <summary>Asks for a level, 0 to 100. The display is changed while the caller goes on.</summary>
    public void RequestLevel(int level)
    {
        if (Volatile.Read(ref _level) < 0)
        {
            return;
        }

        int clampedLevel = Math.Clamp(level, 0, 100);

        lock (_lock)
        {
            _requestedLevel = clampedLevel;
            if (!_applyLoopActive)
            {
                _applyLoopActive = true;
                _ = Task.Run(ApplyLoopAsync);
            }
        }
    }

    private void Resolve(HMONITOR monitor)
    {
        try
        {
            int level;
            IBackend? backend = DdcBackend.TryCreate(monitor, out level);
            backend ??= WmiBackend.TryCreate(out level);
            if (backend is null)
            {
                Logger.W(TAG, "The display does not report that its brightness can be changed.");
                Volatile.Write(ref _level, -1);
                return;
            }

            lock (_lock)
            {
                _backend = backend;
            }

            Volatile.Write(ref _level, level);
        }
        catch (Exception exception)
        {
            Logger.E(TAG, nameof(Resolve), exception);
            Volatile.Write(ref _level, -1);
        }
        finally
        {
            // Nothing found may only mean the display was off, so the next player asks again instead of remembering it.
            Volatile.Write(ref _resolveState, Volatile.Read(ref _level) < 0 ? 0 : 2);
        }
    }

    private async Task ApplyLoopAsync()
    {
        try
        {
            while (true)
            {
                int level;
                IBackend? backend;
                lock (_lock)
                {
                    level = _requestedLevel;
                    _requestedLevel = -1;
                    backend = _backend;

                    if (level < 0)
                    {
                        _applyLoopActive = false;
                        return;
                    }
                }

                if (backend is not null && backend.SetLevel(level))
                {
                    Volatile.Write(ref _level, level);
                }

                // The display is given time to finish a change before it is asked for the next one; a request that
                // arrives meanwhile replaces the one just applied rather than queueing behind it.
                await Task.Delay(MinimumApplyIntervalMilliseconds);
            }
        }
        catch (Exception exception)
        {
            Logger.E(TAG, nameof(ApplyLoopAsync), exception);

            lock (_lock)
            {
                _applyLoopActive = false;
            }
        }
    }

    private interface IBackend
    {
        bool SetLevel(int level);
    }

    /// <summary>The display's own control, reached over the monitor cable.</summary>
    private sealed class DdcBackend : IBackend
    {
        private readonly PHYSICAL_MONITOR[] _monitors;
        private readonly int _index;
        private readonly uint _minimum;
        private readonly uint _maximum;

        private DdcBackend(PHYSICAL_MONITOR[] monitors, int index, uint minimum, uint maximum)
        {
            _monitors = monitors;
            _index = index;
            _minimum = minimum;
            _maximum = maximum;
        }

        public static DdcBackend? TryCreate(HMONITOR monitor, out int level)
        {
            level = -1;

            if (monitor.IsNull || !PInvoke.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0)
            {
                return null;
            }

            var monitors = new PHYSICAL_MONITOR[count];
            if (!PInvoke.GetPhysicalMonitorsFromHMONITOR(monitor, monitors))
            {
                return null;
            }

            try
            {
                for (int i = 0; i < monitors.Length; i++)
                {
                    unsafe
                    {
                        // The friendly overloads take a SafeHandle, and a physical monitor is not one: the handle the
                        // monitor was opened with is kept here and handed to the raw call.
                        uint minimum;
                        uint current;
                        uint maximum;
                        if (PInvoke.GetMonitorBrightness(monitors[i].hPhysicalMonitor, &minimum, &current, &maximum) == 0)
                        {
                            continue;
                        }

                        if (maximum <= minimum)
                        {
                            continue;
                        }

                        level = (int)Math.Round((current - minimum) * 100.0 / (maximum - minimum), MidpointRounding.AwayFromZero);
                        return new DdcBackend(monitors, i, minimum, maximum);
                    }
                }
            }
            catch (Exception exception)
            {
                Logger.W(TAG, $"The display did not answer over DDC/CI: {exception.Message}");
            }

            PInvoke.DestroyPhysicalMonitors(monitors);
            return null;
        }

        public bool SetLevel(int level)
        {
            uint value = _minimum + (uint)Math.Round((_maximum - _minimum) * level / 100.0, MidpointRounding.AwayFromZero);

            try
            {
                return PInvoke.SetMonitorBrightness(_monitors[_index].hPhysicalMonitor, value) != 0;
            }
            catch (Exception exception)
            {
                Logger.W(TAG, $"Unable to change the display brightness over DDC/CI: {exception.Message}");
                return false;
            }
        }
    }

    /// <summary>The panel's own interface, which is what a laptop whose display answers nothing over DDC/CI has.</summary>
    private sealed class WmiBackend : IBackend
    {
        public static WmiBackend? TryCreate(out int level)
        {
            level = -1;

            try
            {
                using ManagementClass brightnessClass = ManagementClassFactory.Instance.GetWmiMonitorBrightnessClass();
                foreach (ManagementObject instance in brightnessClass.GetInstances())
                {
                    using (instance)
                    {
                        if (instance["CurrentBrightness"] is not { } current)
                        {
                            continue;
                        }

                        int value = Convert.ToInt32(current, CultureInfo.InvariantCulture);
                        if (value < 0 || value > 100)
                        {
                            continue;
                        }

                        level = value;
                        return new WmiBackend();
                    }
                }
            }
            catch (ManagementException exception)
            {
                Logger.W(TAG, $"The display did not report its brightness: {exception.Message}");
            }
            catch (COMException exception)
            {
                Logger.W(TAG, $"The display did not report its brightness: {exception.Message}");
            }

            return null;
        }

        public bool SetLevel(int level)
        {
            try
            {
                using ManagementClass methodsClass = ManagementClassFactory.Instance.GetWmiMonitorBrightnessMethodsClass();
                foreach (ManagementObject instance in methodsClass.GetInstances())
                {
                    using (instance)
                    {
                        using ManagementBaseObject parameters = instance.GetMethodParameters("WmiSetBrightness");
                        parameters["Timeout"] = 1u;
                        parameters["Brightness"] = (byte)level;
                        using ManagementBaseObject? result = instance.InvokeMethod("WmiSetBrightness", parameters, null);
                        return result is not null;
                    }
                }
            }
            catch (ManagementException exception)
            {
                Logger.W(TAG, $"Unable to change the display brightness: {exception.Message}");
            }
            catch (COMException exception)
            {
                Logger.W(TAG, $"Unable to change the display brightness: {exception.Message}");
            }

            return false;
        }
    }
}
