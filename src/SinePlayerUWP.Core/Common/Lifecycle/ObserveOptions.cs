// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayerUWP.Core.Common.Lifecycle;

public class ObserveOptions
{
    public bool Sticky { get; init; } = false;
    public LiveDataPublishBehavior PublishBehavior { get; init; } = LiveDataPublishBehavior.Default;
}
