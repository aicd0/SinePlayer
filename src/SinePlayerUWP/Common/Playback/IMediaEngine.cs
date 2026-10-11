// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;

using SinePlayerUWP.Common.ErrorHandling;

namespace SinePlayerUWP.Common.Playback;

// Abstraction over a media playback backend (native MediaPlayer today, other
// engines can be plugged in later). Events are raised on the main thread.
internal interface IMediaEngine : IDisposable
{
    MediaEngineStateEnum State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    double Volume { get; }
    bool IsMuted { get; }
    double PlaybackRate { get; }

    event Action<MediaEngineStateEnum>? StateChanged;
    event Action? PositionChanged;
    event Action? MediaOpened;
    event Action? MediaEnded;
    event Action<string>? ErrorOccurred;

    // Returns once the media source has been attached; the actual open result
    // is reported through MediaOpened / ErrorOccurred afterwards.
    Task<ErrorResult> LoadAsync(string filePath);

    void Play();

    void Pause();

    void Stop();

    void Seek(TimeSpan position);

    void SetVolume(double volume);

    void SetMuted(bool muted);

    void SetPlaybackRate(double rate);

    // Must be called on the main thread. The surface is created once and reused.
    UIElement CreateVideoSurface();
}
