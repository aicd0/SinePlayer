// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Filter;

public interface IAudioFilter : IMediaFilter
{
    AudioStreamInfo? OutputFormat { get; }

    OperationResult Configure(AudioStreamInfo input, AudioStreamInfo target);

    OperationResult Apply(RawAudioBuffer input, out RawAudioBuffer output);
}
