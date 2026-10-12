// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.HotKey;
using SinePlayer.Data.Models.Playback;

namespace SinePlayer.Views.Pages.Player;

internal sealed partial class PlayerTransportBar : BaseUserControl
{
    public delegate void PlayPauseRequestedEventHandler();
    public event PlayPauseRequestedEventHandler? PlayPauseRequested;

    public delegate void PreviousRequestedEventHandler();
    public event PreviousRequestedEventHandler? PreviousRequested;

    public delegate void NextRequestedEventHandler();
    public event NextRequestedEventHandler? NextRequested;

    public delegate void PlayQueueRequestedEventHandler();
    public event PlayQueueRequestedEventHandler? PlayQueueRequested;

    public delegate void RepeatRequestedEventHandler();
    public event RepeatRequestedEventHandler? RepeatRequested;

    public delegate void ShuffleRequestedEventHandler();
    public event ShuffleRequestedEventHandler? ShuffleRequested;

    public delegate void SeekRequestedEventHandler(long positionMs);
    public event SeekRequestedEventHandler? SeekRequested;

    public delegate void VolumeChangedEventHandler(int volume);
    public event VolumeChangedEventHandler? VolumeChanged;

    public delegate void MuteChangedEventHandler(bool isMuted);
    public event MuteChangedEventHandler? MuteChanged;

    public delegate void PinRequestedEventHandler();
    public event PinRequestedEventHandler? PinRequested;

    public delegate void FullscreenRequestedEventHandler();
    public event FullscreenRequestedEventHandler? FullscreenRequested;

    private long _durationMs = 0;
    private bool _isSeekDragging = false;
    private bool _isUpdatingSeekSlider = false;
    private bool _isUpdatingVolumeSlider = false;
    private bool _isMuted = false;
    private int _volume = 100;
    private bool _isPinned = false;
    private RepeatMode _repeatMode = RepeatMode.Off;

    public PlayerTransportBar()
    {
        InitializeComponent();

        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekSliderPointerPressed), true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekSliderPointerReleased), true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekSliderPointerCaptureLost), true);
    }

    public void SetPlaying(bool isPlaying)
    {
        PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
        ShortcutToolTip.Apply(PlayPauseButton, KeyboardShortcutActions.PlayPause, isPlaying ? StringResource.Pause : StringResource.Play);
    }

    public void SetNavigation(bool isPreviousEnabled, bool isNextEnabled)
    {
        PreviousButton.IsEnabled = isPreviousEnabled;
        NextButton.IsEnabled = isNextEnabled;
    }

    public void SetPosition(long positionMs, long durationMs)
    {
        _durationMs = durationMs;

        double maxSeconds = durationMs > 0 ? durationMs / 1000.0 : 1.0;
        double positionSeconds = Math.Clamp(positionMs / 1000.0, 0.0, maxSeconds);

        _isUpdatingSeekSlider = true;
        try
        {
            if (SeekSlider.Maximum != maxSeconds)
            {
                SeekSlider.Maximum = maxSeconds;
            }

            if (!_isSeekDragging && SeekSlider.Value != positionSeconds)
            {
                SeekSlider.Value = positionSeconds;
            }
        }
        finally
        {
            _isUpdatingSeekSlider = false;
        }

        SeekSlider.IsEnabled = durationMs > 0;
        if (!_isSeekDragging)
        {
            UpdatePositionText(positionMs);
        }
    }

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);

        _isUpdatingVolumeSlider = true;
        try
        {
            VolumeSlider.Value = _volume;
        }
        finally
        {
            _isUpdatingVolumeSlider = false;
        }

        UpdateVolumeIcon();
    }

    public void SetMuted(bool isMuted)
    {
        _isMuted = isMuted;
        UpdateVolumeIcon();
        ToolTipService.SetToolTip(MuteButton, isMuted ? StringResource.Unmute : StringResource.Mute);
    }

    public void SetPinned(bool isPinned)
    {
        _isPinned = isPinned;
        PinIcon.Glyph = isPinned ? "\uE77A" : "\uE718";
        ToolTipService.SetToolTip(PinButton, isPinned ? StringResource.Unpin : StringResource.Pin);
    }

    public void SetFullscreen(bool isFullscreen)
    {
        FullscreenIcon.Glyph = isFullscreen ? "\uE73F" : "\uE740";
        ShortcutToolTip.Apply(FullscreenButton, KeyboardShortcutActions.ToggleFullscreen, isFullscreen ? StringResource.ExitFullscreen : StringResource.EnterFullscreen);
    }

    public void SetRepeatMode(RepeatMode repeatMode)
    {
        _repeatMode = repeatMode;
        RepeatIcon.Glyph = repeatMode switch
        {
            RepeatMode.All => "\uE8EE",
            RepeatMode.One => "\uE8ED",
            _ => "\uF5E7",
        };
        ToolTipService.SetToolTip(RepeatButton, repeatMode switch
        {
            RepeatMode.All => StringResource.RepeatAll,
            RepeatMode.One => StringResource.RepeatOne,
            _ => StringResource.RepeatOff,
        });
    }

    public void SetShuffle(bool isShuffle)
    {
        ShuffleButton.IsChecked = isShuffle;
    }

    private void UpdateVolumeIcon()
    {
        string glyph;
        if (_isMuted)
        {
            glyph = "\uE74F";
        }
        else
        {
            glyph = _volume switch
            {
                <= 0 => "\uE992",
                <= 33 => "\uE993",
                <= 66 => "\uE994",
                _ => "\uE995",
            };
        }

        VolumeIcon.Glyph = glyph;
        MuteIcon.Glyph = glyph;
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        PreviousRequested?.Invoke();
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        PlayPauseRequested?.Invoke();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        NextRequested?.Invoke();
    }

    private void OnPlayQueueClick(object sender, RoutedEventArgs e)
    {
        PlayQueueRequested?.Invoke();
    }

    private void OnRepeatClick(object sender, RoutedEventArgs e)
    {
        RepeatRequested?.Invoke();
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e)
    {
        ShuffleRequested?.Invoke();
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        SetMuted(!_isMuted);
        MuteChanged?.Invoke(_isMuted);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        PinRequested?.Invoke();
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e)
    {
        FullscreenRequested?.Invoke();
    }

    private void OnSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePositionText((long)Math.Round(e.NewValue * 1000.0));
        if (_isUpdatingSeekSlider || _isSeekDragging)
        {
            return;
        }

        SeekRequested?.Invoke((long)Math.Round(e.NewValue * 1000.0));
    }

    private void OnVolumeValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingVolumeSlider)
        {
            return;
        }

        int volume = (int)Math.Round(e.NewValue);
        if (_isMuted && volume > 0)
        {
            SetMuted(false);
            MuteChanged?.Invoke(false);
        }

        VolumeChanged?.Invoke(volume);
    }

    private void OnSeekSliderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isSeekDragging = true;
    }

    private void OnSeekSliderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndSeekDrag();
    }

    private void OnSeekSliderPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndSeekDrag();
    }

    private void EndSeekDrag()
    {
        if (!_isSeekDragging)
        {
            return;
        }

        _isSeekDragging = false;
        SeekRequested?.Invoke((long)Math.Round(SeekSlider.Value * 1000.0));
    }

    private void UpdatePositionText(long positionMs)
    {
        PositionTextBlock.Text = $"{FormatTime(positionMs)} / {FormatTime(_durationMs)}";
    }

    private static string FormatTime(long milliseconds)
    {
        if (milliseconds < 0)
        {
            milliseconds = 0;
        }

        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1.0 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}
