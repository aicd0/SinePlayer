// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Info;

public sealed class AudioStreamInfo : MediaStreamInfo
{
    public override MediaStreamKind Kind => MediaStreamKind.Audio;

    public required int SampleRate { get; init; }

    public required int ChannelCount { get; init; }

    public required ChannelLayout ChannelLayout { get; init; }

    public required AudioSampleFormat SampleFormat { get; init; }

    public AudioStreamInfo WithLayout(int channelCount, ChannelLayout channelLayout)
    {
        return new AudioStreamInfo
        {
            Index = Index,
            CodecId = CodecId,
            CodecName = CodecName,
            Profile = Profile,
            Language = Language,
            Title = Title,
            Duration = Duration,
            Bitrate = Bitrate,
            IsDefault = IsDefault,
            SampleRate = SampleRate,
            ChannelCount = channelCount,
            ChannelLayout = channelLayout,
            SampleFormat = SampleFormat,
        };
    }
}
