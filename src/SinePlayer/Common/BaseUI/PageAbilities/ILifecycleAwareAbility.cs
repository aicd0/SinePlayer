// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Common.Lifecycle;

namespace SinePlayer.Common.BaseUI.PageAbilities;

public delegate void PageLifecycleEventHandler(ILifecycle.State state);

internal interface ILifecycleAwareAbility : IPageAbility
{
    void RegisterPageLifecycleHandler(PageLifecycleEventHandler handler);

    void UnregisterPageLifecycleHandler(PageLifecycleEventHandler handler);
}
