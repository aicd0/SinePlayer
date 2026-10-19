// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Info;

public sealed class AudioStreamInfo : MediaStreamInfo
{
    public override MediaStreamKind Kind => MediaStreamKind.Audio;

    public required int SampleRate { get; init; }

    public required int ChannelCount { get; init; }

    public required ChannelLayout ChannelLayout { get; init; }

    /// <summary>Format the decoder is expected to produce for this stream.</summary>
    public required AudioSampleFormat SampleFormat { get; init; }
}
