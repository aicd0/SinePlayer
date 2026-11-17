// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Threading.Tasks;

using SinePlayer.Core.Common.Utils;

using Windows.Foundation;

namespace SinePlayer.Views.Pages.Player;

/// <summary>
/// Turns a press or drag on the picture into one of the four things it can do: seek, change the volume, change the
/// display brightness, or play faster for as long as the pointer is held. A drag's effect is decided by the first
/// movement past the drag threshold — horizontally it is a seek, vertically the side of the picture the drag started
/// on decides — and stays decided until the pointer is lifted, so that a slightly diagonal drag does not switch
/// between them halfway. Holding still without crossing that threshold for <see cref="LongPressMilliseconds"/> is the
/// speed gesture instead.
/// <para>
/// What the gesture started from is kept here rather than by the caller: the amounts are measured from the position
/// the media and the volume had when the drag began, not from what they are by the time a request is handled.
/// </para>
/// </summary>
internal sealed class PlayerGestureController
{
    // Only decides whether there is a gesture at all; the amounts are always measured from where the pointer went down,
    // so crossing it costs the gesture none of its travel.
    private const double DragThresholdDips = 50.0;
    private const double VolumeSensitivity = 2.0;
    private const double BrightnessSensitivity = 2.0;
    private const double SeekSensitivity = 0.4;
    private const long MinimumSeekMilliseconds = 500;
    private const int LongPressMilliseconds = 300;
    private const double SpeedMultiplier = 2.0;

    private uint _pointerId;
    private Mode _mode;
    private Point _startPosition;
    private Point _lastPosition;
    private Context _context;
    private int _appliedVolume;
    private int _appliedBrightness;
    private int _longPressSession;

    /// <summary>A level of 0 to 100 to set the volume to.</summary>
    public event Action<int>? VolumeRequested;

    /// <summary>A level of 0 to 100 to set the display brightness to.</summary>
    public event Action<int>? BrightnessRequested;

    /// <summary>What the gesture is doing, or <see cref="Effect.None"/> once it is over.</summary>
    public event Action<Effect>? EffectChanged;

    /// <summary>Where the media would move to if the pointer were lifted now.</summary>
    public event Action<SeekRequest>? SeekPreviewed;

    /// <summary>Where to move the media to once the pointer is lifted.</summary>
    public event Action<SeekRequest>? SeekRequested;

    /// <summary>The rate to play at: the multiplier while the hold lasts, then the rate it had before.</summary>
    public event Action<double>? SpeedRequested;

    public void Begin(uint pointerId, Point position, Context context)
    {
        // A second pointer landing on the picture does not take over a gesture that is already running.
        if (_mode != Mode.None && pointerId != _pointerId)
        {
            return;
        }

        Reset();

        _pointerId = pointerId;
        _mode = Mode.Pending;
        _startPosition = position;
        _lastPosition = position;
        _context = context;
        _appliedVolume = context.Volume;
        _appliedBrightness = context.Brightness;
        StartLongPress();
    }

    public void Move(uint pointerId, Point position)
    {
        if (_mode == Mode.None || pointerId != _pointerId)
        {
            return;
        }

        _lastPosition = position;

        if (_mode == Mode.Pending)
        {
            double dx = position.X - _startPosition.X;
            double dy = position.Y - _startPosition.Y;
            if (Math.Abs(dx) < DragThresholdDips && Math.Abs(dy) < DragThresholdDips)
            {
                return;
            }

            _mode = Math.Abs(dx) >= Math.Abs(dy)
                ? Mode.Seek
                : _startPosition.X < _context.Width / 2.0 ? Mode.Brightness : Mode.Volume;

            // A display that cannot be adjusted makes the drag a no-op, so nothing of it started to read out.
            if (_mode != Mode.Brightness || _context.Brightness >= 0)
            {
                EffectChanged?.Invoke(ToEffect(_mode));
            }
        }

        switch (_mode)
        {
            case Mode.Volume:
                ApplyVolume();
                break;
            case Mode.Brightness:
                ApplyBrightness();
                break;
            case Mode.Seek:
                if (TryGetSeek(out SeekRequest preview))
                {
                    SeekPreviewed?.Invoke(preview);
                }
                break;
        }
    }

    public void Complete(uint pointerId)
    {
        if (_mode == Mode.None || pointerId != _pointerId)
        {
            return;
        }

        if (_mode == Mode.Seek && TryGetSeek(out SeekRequest request))
        {
            SeekRequested?.Invoke(request);
        }

        Reset();
    }

    public void Cancel(uint pointerId)
    {
        if (_mode == Mode.None || pointerId != _pointerId)
        {
            return;
        }

        // Whatever was already applied stays applied; only a seek, which is applied here, is dropped.
        Reset();
    }

    private void ApplyVolume()
    {
        int volume = GetVerticalValue(_context.Volume, VolumeSensitivity);
        if (volume == _appliedVolume)
        {
            return;
        }

        _appliedVolume = volume;
        VolumeRequested?.Invoke(volume);
    }

    private void StartLongPress()
    {
        int session = ++_longPressSession;

        // The delay resumes on the thread it was started from, which is the UI thread the pointer events arrive on.
        CoroutineUtils.Run(async () =>
        {
            await Task.Delay(LongPressMilliseconds);

            if (session == _longPressSession)
            {
                StartSpeed();
            }
        });
    }

    private void StartSpeed()
    {
        // Anything else the drag turned into, or its end, has already replaced the wait for a hold.
        if (_mode != Mode.Pending)
        {
            return;
        }

        // Faster than the hold would make it: there is nothing to do, and a drag that follows still can be one.
        if (_context.PlaybackRate > SpeedMultiplier)
        {
            return;
        }

        _mode = Mode.Speed;
        EffectChanged?.Invoke(ToEffect(_mode));
        SpeedRequested?.Invoke(SpeedMultiplier);
    }

    private void ApplyBrightness()
    {
        // A display that cannot be adjusted leaves the gesture with nothing to do, rather than falling back to
        // something else.
        if (_context.Brightness < 0)
        {
            return;
        }

        int brightness = GetVerticalValue(_context.Brightness, BrightnessSensitivity);
        if (brightness == _appliedBrightness)
        {
            return;
        }

        _appliedBrightness = brightness;
        BrightnessRequested?.Invoke(brightness);
    }

    private int GetVerticalValue(int startValue, double sensitivity)
    {
        if (_context.Height <= 0)
        {
            return startValue;
        }

        double ratio = (_startPosition.Y - _lastPosition.Y) / _context.Height;
        int delta = (int)Math.Round(ratio * 100.0 * sensitivity, MidpointRounding.AwayFromZero);
        return Math.Clamp(startValue + delta, 0, 100);
    }

    private long GetSeekDelta()
    {
        if (_context.Width <= 0 || _context.DurationMs <= 0)
        {
            return 0;
        }

        double ratio = (_lastPosition.X - _startPosition.X) / _context.Width;
        return (long)Math.Round(ratio * _context.DurationMs * SeekSensitivity, MidpointRounding.AwayFromZero);
    }

    private bool TryGetSeek(out SeekRequest request)
    {
        request = default;

        long deltaMs = GetSeekDelta();
        if (Math.Abs(deltaMs) < MinimumSeekMilliseconds)
        {
            return false;
        }

        // Clamped to the media, so what is left is how far it actually moves; the sign is the direction the drag
        // meant, whatever the position has become by now.
        long offsetMs = Math.Clamp(_context.PositionMs + deltaMs, 0, _context.DurationMs) - _context.PositionMs;
        if (offsetMs == 0)
        {
            return false;
        }

        request = new SeekRequest(_context.PositionMs, offsetMs);
        return true;
    }

    private static Effect ToEffect(Mode mode)
    {
        return mode switch
        {
            Mode.Seek => Effect.Seek,
            Mode.Brightness => Effect.Brightness,
            Mode.Volume => Effect.Volume,
            Mode.Speed => Effect.Speed,
            _ => Effect.None,
        };
    }

    private void Reset()
    {
        _longPressSession++;

        if (_mode == Mode.None)
        {
            return;
        }

        Mode finished = _mode;
        _mode = Mode.None;

        if (finished != Mode.Pending)
        {
            EffectChanged?.Invoke(Effect.None);
        }

        // Said before the rate goes back, so that nothing reads the rate the hold returns to as one it is playing at.
        if (finished == Mode.Speed)
        {
            SpeedRequested?.Invoke(_context.PlaybackRate);
        }
    }

    /// <summary>What the gesture is measured against, taken when the pointer went down.</summary>
    internal readonly record struct Context(
        double Width,
        double Height,
        int Volume,
        long PositionMs,
        long DurationMs,
        int Brightness,
        double PlaybackRate);

    /// <summary>
    /// A seek asked for by a drag: the position the media was at when the gesture began, and how far from it to move,
    /// signed by the direction the drag meant. Where the media is by the time this is handled is not part of it — a
    /// short drag forward may already be behind where playback has got to.
    /// </summary>
    internal readonly record struct SeekRequest(long StartPositionMs, long OffsetMs);

    internal enum Effect
    {
        None,
        Volume,
        Brightness,
        Seek,
        Speed,
    }

    private enum Mode
    {
        None,
        Pending,
        Seek,
        Volume,
        Brightness,
        Speed,
    }
}
