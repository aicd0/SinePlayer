// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using SinePlayerUWP.Common.ErrorHandling;
using SinePlayerUWP.Core.Common.DebugTools;
using SinePlayerUWP.Core.Common.Utils;

using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace SinePlayerUWP.Common.Playback;

internal sealed class NativeMediaEngine : IMediaEngine
{
    private const string TAG = nameof(NativeMediaEngine);

    private readonly MediaPlayer _player = new();

    private MediaPlayerElement? _videoSurface;
    private MediaEngineStateEnum _state = MediaEngineStateEnum.None;
    private bool _isDisposed = false;

    public NativeMediaEngine()
    {
        _player.AutoPlay = false;
        _player.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
        _player.PlaybackSession.PositionChanged += PlaybackSession_PositionChanged;
        _player.MediaOpened += Player_MediaOpened;
        _player.MediaEnded += Player_MediaEnded;
        _player.MediaFailed += Player_MediaFailed;
    }

    public MediaEngineStateEnum State => _state;

    public TimeSpan Position => _player.PlaybackSession.Position;

    public TimeSpan Duration => _player.PlaybackSession.NaturalDuration;

    public double Volume => _player.Volume;

    public bool IsMuted => _player.IsMuted;

    public double PlaybackRate => _player.PlaybackSession.PlaybackRate;

    public event Action<MediaEngineStateEnum>? StateChanged;
    public event Action? PositionChanged;
    public event Action? MediaOpened;
    public event Action? MediaEnded;
    public event Action<string>? ErrorOccurred;

    public async Task<ErrorResult> LoadAsync(string filePath)
    {
        var err = ErrorLogger.Create(TAG);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return err.Error($"Media file not found: '{filePath}'.");
        }

        SetState(MediaEngineStateEnum.Opening);
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
            _player.Source = MediaSource.CreateFromStorageFile(file);
            return err.Success();
        }
        catch (Exception ex)
        {
            Logger.E(TAG, $"Unable to load media file '{filePath}'", ex);
            SetState(MediaEngineStateEnum.Error);
            return err.Error(ex);
        }
    }

    public void Play()
    {
        if (_state == MediaEngineStateEnum.Ended)
        {
            _player.PlaybackSession.Position = TimeSpan.Zero;
        }

        _player.Play();
    }

    public void Pause()
    {
        _player.Pause();
    }

    public void Stop()
    {
        _player.Pause();
        _player.PlaybackSession.Position = TimeSpan.Zero;
    }

    public void Seek(TimeSpan position)
    {
        TimeSpan duration = Duration;
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }
        else if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        _player.PlaybackSession.Position = position;
    }

    public void SetVolume(double volume)
    {
        _player.Volume = Math.Clamp(volume, 0.0, 1.0);
    }

    public void SetMuted(bool muted)
    {
        _player.IsMuted = muted;
    }

    public void SetPlaybackRate(double rate)
    {
        _player.PlaybackSession.PlaybackRate = Math.Clamp(rate, 0.25, 4.0);
    }

    public UIElement CreateVideoSurface()
    {
        if (_videoSurface is null)
        {
            _videoSurface = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Stretch.Uniform,
            };
            _videoSurface.SetMediaPlayer(_player);
        }

        return _videoSurface;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _player.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
        _player.PlaybackSession.PositionChanged -= PlaybackSession_PositionChanged;
        _player.MediaOpened -= Player_MediaOpened;
        _player.MediaEnded -= Player_MediaEnded;
        _player.MediaFailed -= Player_MediaFailed;
        _player.Source = null;
        _videoSurface?.SetMediaPlayer(null);
        _videoSurface = null;
        _player.Dispose();
    }

    private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        switch (sender.PlaybackState)
        {
            case MediaPlaybackState.None:
                SetState(MediaEngineStateEnum.None);
                break;
            case MediaPlaybackState.Opening:
                SetState(MediaEngineStateEnum.Opening);
                break;
            case MediaPlaybackState.Buffering:
                if (_state != MediaEngineStateEnum.Playing && _state != MediaEngineStateEnum.Paused)
                {
                    SetState(MediaEngineStateEnum.Opening);
                }

                break;
            case MediaPlaybackState.Playing:
                SetState(MediaEngineStateEnum.Playing);
                break;
            case MediaPlaybackState.Paused:
                SetState(MediaEngineStateEnum.Paused);
                break;
            default:
                break;
        }
    }

    private void PlaybackSession_PositionChanged(MediaPlaybackSession sender, object args)
    {
        CoroutineUtils.RunInMainThread(() => PositionChanged?.Invoke());
    }

    private void Player_MediaOpened(MediaPlayer sender, object args)
    {
        CoroutineUtils.RunInMainThread(() => MediaOpened?.Invoke());
    }

    private void Player_MediaEnded(MediaPlayer sender, object args)
    {
        SetState(MediaEngineStateEnum.Ended);
        CoroutineUtils.RunInMainThread(() => MediaEnded?.Invoke());
    }

    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        Logger.E(TAG, $"Media playback failed: {args.ErrorMessage}", null);
        SetState(MediaEngineStateEnum.Error);
        CoroutineUtils.RunInMainThread(() => ErrorOccurred?.Invoke(args.ErrorMessage));
    }

    private void SetState(MediaEngineStateEnum state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        CoroutineUtils.RunInMainThread(() => StateChanged?.Invoke(state));
    }
}
