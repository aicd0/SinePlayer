// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayerUWP.SDK.Plugins.Media;

public interface IMediaTagCategory
{
    IReadOnlySet<string> Tags { get; }
}
