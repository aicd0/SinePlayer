// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Pipeline;

internal readonly record struct FrameEntry(
    RawVideoFrame? Frame,
    PipelineVersion Versions,
    long Epoch);
