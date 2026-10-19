// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.Lifecycle;

namespace SinePlayer.Common.BaseUI.PageAbilities;

public delegate void PageLifecycleEventHandler(ILifecycle.State state);

internal interface ILifecycleAwareAbility : IPageAbility
{
    void RegisterPageLifecycleHandler(PageLifecycleEventHandler handler);

    void UnregisterPageLifecycleHandler(PageLifecycleEventHandler handler);
}
