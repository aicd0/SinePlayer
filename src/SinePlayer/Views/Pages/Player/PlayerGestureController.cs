// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.UI.Input;

using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Misc;

using Windows.Foundation;

namespace SinePlayer.Views.Pages.Player;

internal sealed class PlayerGestureController
{
    #region Constructor

    private const double DragThresholdDips = 20.0;
    private const double VolumeSensitivity = 2.0;
    private const double BrightnessSensitivity = 2.0;
    private const double SeekMillisecondsPerDip = 100.0;
    private const int LongPressMilliseconds = 300;
    private const double MaximumPan = 2000.0;

    // Long enough for the second tap of a double tap to take over, short enough not to feel late on its own.
    private const int SingleTapMilliseconds = 100;

    // A pinch is measured from the contacts, so they have to be this far apart to divide by.
    private const double MinimumPinchDistanceDips = 1.0;

    // How far the fingers have to have spread or moved together before it is clear which of the two was meant.
    private const double PinchModeThresholdDips = 10.0;

    private readonly PlayerGestureRecognizer _gestureRecognizer = new();
    private readonly List<Contact> _contacts = [];

    private uint _pointerId;
    private Mode _mode;
    private Point _startPosition;
    private Point _lastPosition;
    private Context _context;
    private int _appliedVolume;
    private int _appliedBrightness;
    private int _longPressSession;
    private int _tapSession;
    private bool _tapPending;
    private bool _multiContactSequence;
    private ZoomPanRequest _zoomPanStart;
    private ZoomPanRequest _zoomPanValue;
    private ZoomPanMode _zoomPanMode;
    private Point _zoomPanCenter;
    private double _zoomPanDistance;

    public PlayerGestureController()
    {
        _gestureRecognizer.Tapped += GestureRecognizer_Tapped;
        _gestureRecognizer.ManipulationStarted += GestureRecognizer_ManipulationStarted;
        _gestureRecognizer.ManipulationUpdated += GestureRecognizer_ManipulationUpdated;
        _gestureRecognizer.ManipulationCompleted += GestureRecognizer_ManipulationCompleted;
    }

    #endregion

    #region Public API

    /// <summary>A tap that did not turn out to be the first of a double tap.</summary>
    public event Action? SingleTapped;

    /// <summary>The second tap of a double tap.</summary>
    public event Action? DoubleTapped;

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

    /// <summary>Where the picture would be zoomed and panned to if the contacts were lifted now.</summary>
    public event Action<ZoomPanRequest>? ZoomPanPreviewed;

    /// <summary>Where to zoom and pan the picture to once the contacts are lifted.</summary>
    public event Action<ZoomPanRequest>? ZoomPanRequested;

    public void Begin(PointerPoint point, Context context)
    {
        _gestureRecognizer.ProcessDownEvent(point);

        uint pointerId = point.PointerId;
        int index = IndexOfContact(pointerId);
        if (index >= 0)
        {
            _contacts[index] = new(pointerId, point.Position);
            return;
        }

        if (_contacts.Count > 0)
        {
            _multiContactSequence = true;
            _contacts.Add(new(pointerId, point.Position));

            // A third contact joins a pinch without disturbing it, since the first two are what it is measured by.
            if (_mode != Mode.ZoomPan)
            {
                // Otherwise this is the second contact, and what the first one began stops here. The pinch that may
                // follow is measured against the newest snapshot, since it starts from the picture as it is now.
                _context = context;
                Reset();
            }

            return;
        }

        _multiContactSequence = false;
        _contacts.Add(new(pointerId, point.Position));

        Reset();

        _pointerId = pointerId;
        _mode = Mode.Pending;
        _startPosition = point.Position;
        _lastPosition = point.Position;
        _context = context;
        _appliedVolume = context.Volume;
        _appliedBrightness = context.Brightness;

        StartLongPress();
    }

    public void Move(IList<PointerPoint> points)
    {
        if (points.Count == 0)
        {
            return;
        }

        // The last one is the point the event is about; the rest are the movement the platform buffered before it.
        PointerPoint point = points[points.Count - 1];

        int index = IndexOfContact(point.PointerId);
        if (index >= 0)
        {
            _contacts[index] = new(point.PointerId, point.Position);
        }

        // After the contacts are current, since an event raised here reads them.
        _gestureRecognizer.ProcessMoveEvents(points);

        if (_mode == Mode.None || point.PointerId != _pointerId)
        {
            return;
        }

        Point position = point.Position;
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

    public void Complete(PointerPoint point)
    {
        int index = RemoveContact(point.PointerId);
        if (index < 0)
        {
            // Nothing was recorded for this pointer, so nothing of it reached the recognizer either.
            return;
        }

        // Either contact of the pair leaving ends the pinch, whatever the recognizer goes on to say.
        if (index < 2)
        {
            EndZoomPan();
        }

        // Before the gesture is reset, so that letting go of a hold is still known to be one and not a click.
        _gestureRecognizer.ProcessUpEvent(point);

        if (_mode == Mode.None || point.PointerId != _pointerId)
        {
            return;
        }

        if (_mode == Mode.Seek && TryGetSeek(out SeekRequest request))
        {
            SeekRequested?.Invoke(request);
        }

        Reset();
    }

    public void Cancel(PointerPoint point)
    {
        int index = RemoveContact(point.PointerId);
        if (index < 0)
        {
            return;
        }

        // Ended rather than lifted, so the recognizer drops the interaction without it becoming a tap.
        _gestureRecognizer.CompleteGesture();

        if (index < 2)
        {
            EndZoomPan();
        }

        if (_mode == Mode.None || point.PointerId != _pointerId)
        {
            return;
        }

        // Whatever was already applied stays applied; only a seek, which is applied here, is dropped.
        Reset();
    }

    #endregion

    #region Recognizer

    private void GestureRecognizer_Tapped(GestureRecognizer sender, TappedEventArgs args)
    {
        // Two contacts on the picture are a pinch, and letting go of a hold is not a click.
        if (_multiContactSequence || _mode == Mode.Speed)
        {
            return;
        }

        if (args.TapCount == 2)
        {
            _tapPending = false;
            _tapSession++;
            DoubleTapped?.Invoke();
            return;
        }

        if (_tapPending)
        {
            return;
        }

        _tapPending = true;
        int session = ++_tapSession;

        // The delay resumes on the thread it was started from, which is the UI thread the pointer events arrive on.
        CoroutineUtils.Run(async () =>
        {
            await Task.Delay(SingleTapMilliseconds);

            // A second tap that made it a double one has already taken over, and a pinch is not a click either.
            if (session != _tapSession || _multiContactSequence)
            {
                return;
            }

            _tapPending = false;
            SingleTapped?.Invoke();
        });
    }

    private void GestureRecognizer_ManipulationStarted(GestureRecognizer sender, ManipulationStartedEventArgs args)
    {
        if (_contacts.Count >= 2)
        {
            BeginZoomPan();
        }
    }

    private void GestureRecognizer_ManipulationUpdated(GestureRecognizer sender, ManipulationUpdatedEventArgs args)
    {
        if (_mode != Mode.ZoomPan)
        {
            // A drag that a second contact turned into a pinch begins here, since the recognizer started on the first.
            if (_contacts.Count >= 2)
            {
                BeginZoomPan();
            }

            return;
        }

        if (_contacts.Count < 2)
        {
            EndZoomPan();
            return;
        }

        UpdateZoomPan();
    }

    private void GestureRecognizer_ManipulationCompleted(GestureRecognizer sender, ManipulationCompletedEventArgs args)
    {
        EndZoomPan();
    }

    #endregion

    #region Zoom and pan

    private void BeginZoomPan()
    {
        if (_mode == Mode.ZoomPan || _contacts.Count < 2 || !_context.HasVideoSurface)
        {
            return;
        }

        // Everything the pinch works out lives in the surface's pixels, which is the unit the picture is placed in
        // and the unit its pan is stored in; only the contacts are in DIPs, and they are converted here.
        double pixelScale = GetPixelScale();
        Point first = ToPixels(_contacts[0].Position, pixelScale);
        Point second = ToPixels(_contacts[1].Position, pixelScale);

        // The pinch is measured from here, so it does not start until the contacts are far enough apart to divide by.
        double distance = GetDistance(first, second);
        if (distance < MinimumPinchDistanceDips * pixelScale)
        {
            return;
        }

        _zoomPanStart = new(_context.Zoom, _context.PanX, _context.PanY);
        _zoomPanValue = _zoomPanStart;
        _zoomPanMode = ZoomPanMode.None;
        _zoomPanCenter = new((first.X + second.X) / 2.0, (first.Y + second.Y) / 2.0);
        _zoomPanDistance = distance;
        _mode = Mode.ZoomPan;
    }

    private void UpdateZoomPan()
    {
        double pixelScale = GetPixelScale();
        Point first = ToPixels(_contacts[0].Position, pixelScale);
        Point second = ToPixels(_contacts[1].Position, pixelScale);
        Point center = new((first.X + second.X) / 2.0, (first.Y + second.Y) / 2.0);
        double distance = GetDistance(first, second);

        if (_zoomPanMode == ZoomPanMode.None && !TryChooseZoomPanMode(center, distance, pixelScale))
        {
            return;
        }

        double startZoom = GetStartZoom();
        double zoom = startZoom;
        double panX = _zoomPanStart.PanX;
        double panY = _zoomPanStart.PanY;

        if (_zoomPanMode == ZoomPanMode.Zoom)
        {
            double scale = distance / _zoomPanDistance;
            if (!double.IsFinite(scale) || scale <= 0.0)
            {
                scale = 1.0;
            }

            zoom = Math.Round(Math.Clamp(startZoom * scale, VideoSettingsModel.MinimumZoom, VideoSettingsModel.MaximumZoom), 2);

            // The point that was between the fingers stays between them: what the new scale did not keep of its
            // offset from the picture's centre is added to the pan, which is how a zoom reaches where it was aimed.
            // This is the only pan a zoom does; the fingers moving together is left to the pan gesture.
            double ratio = zoom / startZoom;
            double anchorX = _zoomPanCenter.X - (_context.Width * pixelScale / 2.0) - _zoomPanStart.PanX;
            double anchorY = _zoomPanCenter.Y - (_context.Height * pixelScale / 2.0) - _zoomPanStart.PanY;
            panX = GetPan(panX, anchorX * (1.0 - ratio));
            panY = GetPan(panY, anchorY * (1.0 - ratio));
        }
        else
        {
            // The spread is what is ignored here, so the picture keeps its zoom and follows the fingers across.
            panX = GetPan(panX, center.X - _zoomPanCenter.X);
            panY = GetPan(panY, center.Y - _zoomPanCenter.Y);
        }

        ZoomPanRequest value = new(zoom, panX, panY);
        if (value == _zoomPanValue)
        {
            return;
        }

        _zoomPanValue = value;
        ZoomPanPreviewed?.Invoke(value);
    }

    /// <summary>
    /// Decides what the pinch is: the fingers spreading apart is a zoom, the two of them moving together is a pan.
    /// Whichever changed more decides it, once, for the rest of the gesture.
    /// </summary>
    private bool TryChooseZoomPanMode(Point center, double distance, double pixelScale)
    {
        double spreadChange = Math.Abs(distance - _zoomPanDistance);
        double centerChange = GetDistance(center, _zoomPanCenter);
        if (Math.Max(spreadChange, centerChange) < PinchModeThresholdDips * pixelScale)
        {
            // Too small a movement to tell the two apart.
            return false;
        }

        _zoomPanMode = spreadChange >= centerChange ? ZoomPanMode.Zoom : ZoomPanMode.Pan;

        // Measured from where the pinch began, so the movement that decided the mode is applied rather than dropped.
        EffectChanged?.Invoke(_zoomPanMode == ZoomPanMode.Zoom ? Effect.Zoom : Effect.Pan);
        return true;
    }

    private void EndZoomPan()
    {
        if (_mode != Mode.ZoomPan)
        {
            return;
        }

        // The picture already shows this much, so an interrupted pinch keeps it rather than leaving the screen and
        // the settings saying different things.
        ZoomPanRequest value = _zoomPanValue;
        Reset();

        // What is on the picture now is what a pinch later in the same sequence has to be measured against.
        _context = _context with { Zoom = value.Zoom, PanX = value.PanX, PanY = value.PanY };

        if (value != _zoomPanStart)
        {
            ZoomPanRequested?.Invoke(value);
        }
    }

    private static double GetPan(double startPan, double changePixels)
    {
        return Math.Round(Math.Clamp(startPan + changePixels, -MaximumPan, MaximumPan), 2);
    }

    private double GetPixelScale()
    {
        double scale = _context.RasterizationScale;
        return double.IsFinite(scale) && scale > 0.0 ? scale : 1.0;
    }

    private static Point ToPixels(Point point, double pixelScale)
    {
        return new Point(point.X * pixelScale, point.Y * pixelScale);
    }

    private double GetStartZoom()
    {
        double zoom = _zoomPanStart.Zoom;
        return double.IsFinite(zoom) && zoom > 0.0 ? zoom : 1.0;
    }

    private static double GetDistance(Point first, Point second)
    {
        double dx = second.X - first.X;
        double dy = second.Y - first.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    #endregion

    #region Drag

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

        double speed = AppSettingsModel.FastForwardSpeed;
        if (!double.IsFinite(speed) || speed <= 0.0)
        {
            return;
        }

        // Faster than the hold would make it: there is nothing to do, and a drag that follows still can be one.
        if (_context.PlaybackRate > speed)
        {
            return;
        }

        _mode = Mode.Speed;
        EffectChanged?.Invoke(ToEffect(_mode));
        SpeedRequested?.Invoke(speed);
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
        double deltaMs = (_lastPosition.X - _startPosition.X) * SeekMillisecondsPerDip;
        return (long)Math.Round(deltaMs, MidpointRounding.AwayFromZero);
    }

    private bool TryGetSeek(out SeekRequest request)
    {
        request = default;

        // Clamped to the media, so what is left is how far it actually moves; the sign is the direction the drag
        // meant, whatever the position has become by now.
        long offsetMs = Math.Clamp(_context.PositionMs + GetSeekDelta(), 0, _context.DurationMs) - _context.PositionMs;
        if (offsetMs == 0)
        {
            return false;
        }

        request = new SeekRequest(_context.PositionMs, offsetMs);
        return true;
    }

    #endregion

    #region Contacts

    private int IndexOfContact(uint pointerId)
    {
        for (int i = 0; i < _contacts.Count; i++)
        {
            if (_contacts[i].PointerId == pointerId)
            {
                return i;
            }
        }

        return -1;
    }

    private int RemoveContact(uint pointerId)
    {
        int index = IndexOfContact(pointerId);
        if (index >= 0)
        {
            _contacts.RemoveAt(index);
        }

        return index;
    }

    #endregion

    #region Misc

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

    #endregion

    #region Types

    /// <summary>What the gesture is measured against, taken when the pointer went down.</summary>
    internal readonly record struct Context
    {
        public double Width { get; init; }
        public double Height { get; init; }
        public int Volume { get; init; }
        public long PositionMs { get; init; }
        public long DurationMs { get; init; }
        public int Brightness { get; init; }
        public double PlaybackRate { get; init; }

        /// <summary>Whether a video track is being drawn, which is the only thing there is to zoom and pan.</summary>
        public bool HasVideoSurface { get; init; }

        /// <summary>
        /// Physical pixels per DIP of the surface, which is the unit the picture's zoom and pan are measured in.
        /// </summary>
        public double RasterizationScale { get; init; }

        public double Zoom { get; init; }
        public double PanX { get; init; }
        public double PanY { get; init; }
    }

    /// <summary>
    /// A seek asked for by a drag: the position the media was at when the gesture began, and how far from it to move,
    /// signed by the direction the drag meant. Where the media is by the time this is handled is not part of it — a
    /// short drag forward may already be behind where playback has got to.
    /// </summary>
    internal readonly record struct SeekRequest(long StartPositionMs, long OffsetMs);

    /// <summary>Where a pinch would leave the picture: how much of its own size it takes up, and how far from the centre of the surface it sits.</summary>
    internal readonly record struct ZoomPanRequest(double Zoom, double PanX, double PanY);

    internal enum Effect
    {
        None,
        Volume,
        Brightness,
        Seek,
        Speed,
        Zoom,
        Pan,
    }

    private enum Mode
    {
        None,
        Pending,
        Seek,
        Volume,
        Brightness,
        Speed,
        ZoomPan,
    }

    private readonly record struct Contact(uint PointerId, Point Position);

    private enum ZoomPanMode
    {
        /// <summary>Not decided yet: the fingers have not moved enough to say whether this is a zoom or a pan.</summary>
        None,
        Zoom,
        Pan,
    }

    #endregion
}
