// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Utils;

using Windows.Win32;

namespace SinePlayer.Core.Playback.Timing;

/// <summary>
/// Asks the operating system for a timer resolution of a millisecond while playback is running, and gives it back when
/// nothing needs it any more.
/// <para>
/// Every wait the engine makes with a timeout is a millisecond one — the picture's wait for the next frame above all —
/// and a process that has not asked for a finer timer gets its waits rounded up to the system tick, which is about
/// fifteen and a half milliseconds. On a sixty hertz picture that is most of a frame: a wait that was asked to end two
/// milliseconds before a frame is due can end after the frame was due, and two such waits in a row leave frames queued
/// long enough for the clock to pass the forty millisecond late tolerance and drop them. Measured on the same wait, a
/// fourteen millisecond timeout ends within 1.2 ms (p90 1.0 ms) with this held, and within 15.5 ms (p90 14.4 ms)
/// without it.
/// </para>
/// <para>
/// Holding it is a request, not a setting: it is counted so that two pipelines sharing the process raise it once and
/// lower it once, and released as soon as the state that wanted it is gone, so nothing pays for it while paused.
/// </para>
/// </summary>
internal static class TimerResolution
{
    private const string TAG = nameof(TimerResolution);

    private const uint Milliseconds = 1;

    private static readonly Lock _lock = new();

    private static int _holders;
    private static bool _raised;

    /// <summary>How many holds are outstanding, which is what a test reads to see the request released.</summary>
    internal static int Holders
    {
        get
        {
            lock (_lock)
            {
                return _holders;
            }
        }
    }

    /// <summary>Whether the process is currently asking for the finer resolution.</summary>
    internal static bool IsRaised
    {
        get
        {
            lock (_lock)
            {
                return _raised;
            }
        }
    }

    internal static void Hold()
    {
        lock (_lock)
        {
            _holders++;
            if (_holders > 1 || _raised)
            {
                return;
            }

            try
            {
                _raised = PInvoke.timeBeginPeriod(Milliseconds) == 0;
                if (!_raised)
                {
                    PlaybackLogger.W(TAG, "The system refused a finer timer resolution; waits will end on its own tick.");
                }
            }
            catch (Exception exception)
            {
                PlaybackLogger.W(TAG, $"The system could not be asked for a finer timer resolution: {exception.Message}");
            }
        }
    }

    internal static void Release()
    {
        lock (_lock)
        {
            if (_holders == 0)
            {
                return;
            }

            _holders--;
            if (_holders > 0 || !_raised)
            {
                return;
            }

            _raised = false;
            try
            {
                PInvoke.timeEndPeriod(Milliseconds);
            }
            catch (Exception exception)
            {
                PlaybackLogger.W(TAG, $"The finer timer resolution could not be given back: {exception.Message}");
            }
        }
    }
}
