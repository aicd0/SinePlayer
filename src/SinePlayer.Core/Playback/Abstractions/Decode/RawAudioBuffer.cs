// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

/// <summary>
/// A decoded block of audio samples, already in the format the renderer will consume.
/// </summary>
public sealed class RawAudioBuffer : IDisposable
{
    public required TimeSpan Timestamp { get; init; }

    public required int SampleRate { get; init; }

    public required int ChannelCount { get; init; }

    public required ChannelLayout ChannelLayout { get; init; }

    public required AudioSampleFormat SampleFormat { get; init; }

    public required int SampleCount { get; init; }

    public required IMediaBuffer Buffer { get; init; }

    public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds((double)SampleCount / SampleRate) : TimeSpan.Zero;

    public void Dispose()
    {
        Buffer.Dispose();
    }
}
