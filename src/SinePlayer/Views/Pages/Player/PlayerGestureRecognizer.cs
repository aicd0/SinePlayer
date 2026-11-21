// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Microsoft.UI.Input;

using SinePlayer.Core.Common.DebugTools;

namespace SinePlayer.Views.Pages.Player;

internal class PlayerGestureRecognizer
{
    private const string TAG = nameof(PlayerGestureRecognizer);

    private readonly GestureRecognizer _gestureRecognizer = new();

    public PlayerGestureRecognizer()
    {
        _gestureRecognizer.GestureSettings =
            GestureSettings.Tap |
            GestureSettings.DoubleTap |
            GestureSettings.ManipulationScale |
            GestureSettings.ManipulationTranslateX |
            GestureSettings.ManipulationTranslateY;
        _gestureRecognizer.Tapped += GestureRecognizer_Tapped;
        _gestureRecognizer.ManipulationStarted += GestureRecognizer_ManipulationStarted;
        _gestureRecognizer.ManipulationUpdated += GestureRecognizer_ManipulationUpdated;
        _gestureRecognizer.ManipulationCompleted += GestureRecognizer_ManipulationCompleted;
    }

    public delegate void TappedHandler(GestureRecognizer sender, TappedEventArgs e);
    public delegate void ManipulationStartedHandler(GestureRecognizer sender, ManipulationStartedEventArgs e);
    public delegate void ManipulationUpdatedHandler(GestureRecognizer sender, ManipulationUpdatedEventArgs e);
    public delegate void ManipulationCompletedHandler(GestureRecognizer sender, ManipulationCompletedEventArgs e);

    public event TappedHandler? Tapped;
    public event ManipulationStartedHandler? ManipulationStarted;
    public event ManipulationUpdatedHandler? ManipulationUpdated;
    public event ManipulationCompletedHandler? ManipulationCompleted;

    public void ProcessDownEvent(PointerPoint value)
    {
        try
        {
            _gestureRecognizer.ProcessDownEvent(value);
        }
        catch (ArgumentException ex)
        {
            Logger.E(TAG, nameof(ProcessDownEvent), ex);
        }
        catch (Exception ex)
        {
            Logger.F(TAG, nameof(ProcessDownEvent), ex);
        }
    }

    public void ProcessMoveEvents(IList<PointerPoint> value)
    {
        try
        {
            _gestureRecognizer.ProcessMoveEvents(value);
        }
        catch (ArgumentException ex)
        {
            Logger.E(TAG, nameof(ProcessMoveEvents), ex);
        }
        catch (COMException ex)
        {
            Logger.E(TAG, nameof(ProcessMoveEvents), ex);
        }
        catch (Exception ex)
        {
            Logger.F(TAG, nameof(ProcessMoveEvents), ex);
        }
    }

    public void ProcessUpEvent(PointerPoint value)
    {
        try
        {
            _gestureRecognizer.ProcessUpEvent(value);
        }
        catch (ArgumentException ex)
        {
            Logger.E(TAG, nameof(ProcessUpEvent), ex);
        }
        catch (Exception ex)
        {
            Logger.F(TAG, nameof(ProcessUpEvent), ex);
        }
    }

    public void CompleteGesture()
    {
        try
        {
            _gestureRecognizer.CompleteGesture();
        }
        catch (ArgumentException ex)
        {
            Logger.E(TAG, nameof(CompleteGesture), ex);
        }
        catch (Exception ex)
        {
            Logger.F(TAG, nameof(CompleteGesture), ex);
        }
    }

    private void GestureRecognizer_Tapped(GestureRecognizer sender, TappedEventArgs args)
    {
        try
        {
            Tapped?.Invoke(sender, args);
        }
        catch (Exception ex)
        {
            Logger.F(TAG, nameof(GestureRecognizer_Tapped), ex);
        }
    }

    private void GestureRecognizer_ManipulationStarted(GestureRecognizer sender, ManipulationStartedEventArgs args)
    {
        try
        {
            ManipulationStarted?.Invoke(sender, args);
        }
        catch (Exception ex)
        {
            Logger.F(TAG, nameof(GestureRecognizer_ManipulationStarted), ex);
        }
    }

    private void GestureRecognizer_ManipulationUpdated(GestureRecognizer sender, ManipulationUpdatedEventArgs args)
    {
        try
        {
            ManipulationUpdated?.Invoke(sender, args);
        }
        catch (Exception ex)
        {
            Logger.F(TAG, nameof(GestureRecognizer_ManipulationUpdated), ex);
        }
    }

    private void GestureRecognizer_ManipulationCompleted(GestureRecognizer sender, ManipulationCompletedEventArgs args)
    {
        try
        {
            ManipulationCompleted?.Invoke(sender, args);
        }
        catch (Exception ex)
        {
            Logger.F(TAG, nameof(GestureRecognizer_ManipulationCompleted), ex);
        }
    }
}
