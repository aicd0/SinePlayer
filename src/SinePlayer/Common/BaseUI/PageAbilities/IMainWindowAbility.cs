// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Drawing;

using Microsoft.UI.Input;

using SinePlayer.Common.Plugins;
using SinePlayer.Core.Common.Lifecycle;

namespace SinePlayer.Common.BaseUI.PageAbilities;

internal interface IMainWindowAbility : IPageAbility
{
    public delegate void MinimizeChangedEventHandler(bool isMinimized);

    public delegate void FullscreenChangedEventHandler(bool isFullscreen);

    public delegate void PointerInsideChangedEventHandler(bool isPointerInside);

    public delegate void LastPointerDeviceTypeChangedEventHandler(PointerDeviceType deviceType);

    bool IsActive { get; }

    bool IsFullscreen { get; }

    bool IsMinimized { get; }

    PluginWindowContext PluginWindowContext { get; }

    int WindowId { get; }

    SizeF WindowSize { get; }

    bool IsFocusLocked { get; }

    PointerDeviceType LastPointerDeviceType { get; }

    void EnterFullscreen();

    void ExitFullscreen();

    PointF GetPointerPosition();

    double GetRasterizationScale();

    void RegisterFullscreenChangedHandler(ILifecycleOwner owner, FullscreenChangedEventHandler handler);

    void RegisterMinimizeChangedHandler(ILifecycleOwner owner, MinimizeChangedEventHandler handler);

    void RegisterPointerInsideRootElementChangedHandler(ILifecycleOwner owner, PointerInsideChangedEventHandler handler);

    void RegisterLastPointerDeviceTypeChangedHandler(ILifecycleOwner owner, LastPointerDeviceTypeChangedEventHandler handler);

    void RequestFocusLock();

    void ReleaseFocusLock();

}
