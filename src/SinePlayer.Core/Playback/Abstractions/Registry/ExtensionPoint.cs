// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Abstractions.Registry;

/// <summary>
/// The roles a component can be registered for. Each value corresponds to one stage of the pipeline,
/// so adding a role here is what makes a new kind of pluggable component possible.
/// </summary>
public enum ExtensionPoint
{
    SourceProvider,
    VideoDecoder,
    AudioDecoder,
    SubtitleDecoder,
    VideoRenderer,
    AudioRenderer,
    SubtitleRenderer,
    VideoFilter,
    AudioFilter,
}
