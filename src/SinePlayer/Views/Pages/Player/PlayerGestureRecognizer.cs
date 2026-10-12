// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

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
        _gestureRecognizer.GestureSettings = GestureSettings.Tap | GestureSettings.DoubleTap;
        _gestureRecognizer.Tapped += GestureRecognizer_Tapped;
    }

    public delegate void TappedHandler(GestureRecognizer sender, TappedEventArgs e);

    public event TappedHandler? Tapped;

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
}
