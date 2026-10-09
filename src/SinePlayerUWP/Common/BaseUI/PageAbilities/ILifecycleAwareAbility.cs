// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Core.Common.Lifecycle;

namespace SinePlayerUWP.Common.BaseUI.PageAbilities;

public delegate void PageLifecycleEventHandler(ILifecycle.State state);

internal interface ILifecycleAwareAbility : IPageAbility
{
    void RegisterPageLifecycleHandler(PageLifecycleEventHandler handler);

    void UnregisterPageLifecycleHandler(PageLifecycleEventHandler handler);
}
