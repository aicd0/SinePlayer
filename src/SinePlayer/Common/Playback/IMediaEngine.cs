// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;

using SinePlayer.Common.ErrorHandling;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback;

internal interface IMediaEngine : IDisposable
{
    //
    // Properties
    //

    MediaEngineState State { get; }

    TimeSpan Position { get; }

    TimeSpan Duration { get; }

    double Volume { get; }

    bool IsMuted { get; }

    double PlaybackRate { get; }

    IPlaybackInfoProvider PlaybackInfoProvider { get; }

    //
    // Events
    //

    event Action<MediaEngineState>? StateChanged;

    event Action? PositionChanged;

    event Action? MediaOpened;

    event Action? MediaEnded;

    event Action<string>? ErrorOccurred;

    //
    // Methods
    //

    Task<ErrorResult> Load(string filePath);

    void Play();

    void Pause();

    void Stop();

    void Seek(TimeSpan position);

    void SetVolume(double volume);

    void SetMuted(bool muted);

    void SetPlaybackRate(double rate);

    UIElement CreateVideoSurface();
}
