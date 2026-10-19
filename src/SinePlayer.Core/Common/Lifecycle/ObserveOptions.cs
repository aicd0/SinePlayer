// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Common.Lifecycle;

public class ObserveOptions
{
    public bool Sticky { get; init; } = false;
    public LiveDataPublishBehavior PublishBehavior { get; init; } = LiveDataPublishBehavior.Default;
}
