// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.SDK.Plugins.Media;

public interface IMediaTagCategory
{
    IReadOnlySet<string> Tags { get; }
}
