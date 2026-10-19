// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Xaml.Input;

using SinePlayer.Core.Common.DebugTools;

namespace SinePlayer.Common.Misc;

internal static class FocusTracker
{
    private const string TAG = nameof(FocusTracker);

    private static readonly LogTag _gotFocusTag = LogTag.N(TAG, "GotFocus");
    private static readonly LogTag _losingFocusTag = LogTag.N(TAG, "LosingFocus");
    private static readonly LogTag _lostFocusTag = LogTag.N(TAG, "LostFocus");

    public static void Initialize()
    {
        FocusManager.GotFocus += FocusManager_GotFocus;
        FocusManager.LosingFocus += FocusManager_LosingFocus;
        FocusManager.LostFocus += FocusManager_LostFocus;
    }

    private static void FocusManager_GotFocus(object? sender, FocusManagerGotFocusEventArgs e)
    {
        Logger.D(_gotFocusTag, $"Ele={e.NewFocusedElement}");
    }

    private static void FocusManager_LosingFocus(object? sender, LosingFocusEventArgs e)
    {
        Logger.D(_losingFocusTag, $"St={e.FocusState},D={e.Direction},OldEle={e.OldFocusedElement},NewEle={e.NewFocusedElement}");
    }

    private static void FocusManager_LostFocus(object? sender, FocusManagerLostFocusEventArgs e)
    {
        Logger.D(_lostFocusTag, $"Ele={e.OldFocusedElement}");
    }
}
