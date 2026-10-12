// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Common.HotKey;

namespace SinePlayerUWP.Views.Pages.Player;

internal sealed partial class PlayerTransportBar : BaseUserControl
{
    public delegate void PlayPauseRequestedEventHandler();
    public event PlayPauseRequestedEventHandler? PlayPauseRequested;

    public delegate void PreviousRequestedEventHandler();
    public event PreviousRequestedEventHandler? PreviousRequested;

    public delegate void NextRequestedEventHandler();
    public event NextRequestedEventHandler? NextRequested;

    public delegate void PlaylistRequestedEventHandler();
    public event PlaylistRequestedEventHandler? PlaylistRequested;

    public delegate void MoreRequestedEventHandler();
    public event MoreRequestedEventHandler? MoreRequested;

    public delegate void SeekRequestedEventHandler(long positionMs);
    public event SeekRequestedEventHandler? SeekRequested;

    public delegate void VolumeChangedEventHandler(double volume);
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
    private bool _isPinned = false;

    public PlayerTransportBar()
    {
        InitializeComponent();

        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekSliderPointerPressed), true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekSliderPointerReleased), true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekSliderPointerCaptureLost), true);
    }

    public FrameworkElement MoreButton => AbbMore;

    public void SetPlaying(bool isPlaying)
    {
        FiPlayPause.Glyph = isPlaying ? "\uE769" : "\uE768";
        ShortcutToolTip.Apply(AbbPlayPause, KeyboardShortcutActions.PlayPause, isPlaying ? StringResource.Pause : StringResource.Play);
    }

    public void SetNavigation(bool isPreviousEnabled, bool isNextEnabled)
    {
        AbbPrevious.IsEnabled = isPreviousEnabled;
        AbbNext.IsEnabled = isNextEnabled;
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

    public void SetVolume(double volume)
    {
        _isUpdatingVolumeSlider = true;
        try
        {
            VolumeSlider.Value = Math.Clamp(volume, 0.0, 1.0);
        }
        finally
        {
            _isUpdatingVolumeSlider = false;
        }
    }

    public void SetMuted(bool isMuted)
    {
        _isMuted = isMuted;
        FiMute.Glyph = isMuted ? "\uE74F" : "\uE767";
    }

    public void SetPinned(bool isPinned)
    {
        _isPinned = isPinned;
        FiPin.Glyph = isPinned ? "\uE77A" : "\uE718";
        ToolTipService.SetToolTip(AbbPin, isPinned ? StringResource.Unpin : StringResource.Pin);
    }

    public void SetFullscreen(bool isFullscreen)
    {
        FiFullscreen.Glyph = isFullscreen ? "\uE73F" : "\uE740";
        ShortcutToolTip.Apply(AbbFullscreen, KeyboardShortcutActions.ToggleFullscreen, isFullscreen ? StringResource.ExitFullscreen : StringResource.EnterFullscreen);
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

    private void OnPlaylistClick(object sender, RoutedEventArgs e)
    {
        PlaylistRequested?.Invoke();
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        MoreRequested?.Invoke();
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

        if (_isMuted && e.NewValue > 0.0)
        {
            SetMuted(false);
            MuteChanged?.Invoke(false);
        }

        VolumeChanged?.Invoke(e.NewValue);
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
        TbPosition.Text = $"{FormatTime(positionMs)} / {FormatTime(_durationMs)}";
        int percentage = _durationMs > 0 ? (int)Math.Clamp(positionMs * 100 / _durationMs, 0, 100) : 0;
        TbPositionSecondary.Text = $"{percentage}%";
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
