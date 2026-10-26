// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.ComponentModel;

using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Lifecycle.Utils;

namespace SinePlayer.UserControls.PlayerSettings;

internal partial class PlayerSettingsPanelViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly MutableLiveData<bool> _settingsChangedLiveData = new();
    public readonly MutableLiveDataWithDelay<bool> SettingsChangedLiveData;

    private string _videoTabTitle = string.Empty;
    public string VideoTabTitle
    {
        get => _videoTabTitle;
        set
        {
            _videoTabTitle = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VideoTabTitle)));
        }
    }

    private string _audioTabTitle = string.Empty;
    public string AudioTabTitle
    {
        get => _audioTabTitle;
        set
        {
            _audioTabTitle = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioTabTitle)));
        }
    }

    private string _subtitleTabTitle = string.Empty;
    public string SubtitleTabTitle
    {
        get => _subtitleTabTitle;
        set
        {
            _subtitleTabTitle = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubtitleTabTitle)));
        }
    }

    private string _videoDecoderLabel = string.Empty;
    public string VideoDecoderLabel
    {
        get => _videoDecoderLabel;
        set
        {
            _videoDecoderLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VideoDecoderLabel)));
        }
    }

    private string _videoRendererLabel = string.Empty;
    public string VideoRendererLabel
    {
        get => _videoRendererLabel;
        set
        {
            _videoRendererLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VideoRendererLabel)));
        }
    }

    private string _videoPreferHardwareDecodingLabel = string.Empty;
    public string VideoPreferHardwareDecodingLabel
    {
        get => _videoPreferHardwareDecodingLabel;
        set
        {
            _videoPreferHardwareDecodingLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VideoPreferHardwareDecodingLabel)));
        }
    }

    private string _videoStretchModeLabel = string.Empty;
    public string VideoStretchModeLabel
    {
        get => _videoStretchModeLabel;
        set
        {
            _videoStretchModeLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VideoStretchModeLabel)));
        }
    }

    private string _videoRotationLabel = string.Empty;
    public string VideoRotationLabel
    {
        get => _videoRotationLabel;
        set
        {
            _videoRotationLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VideoRotationLabel)));
        }
    }

    private string _videoFlipHorizontalLabel = string.Empty;
    public string VideoFlipHorizontalLabel
    {
        get => _videoFlipHorizontalLabel;
        set
        {
            _videoFlipHorizontalLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VideoFlipHorizontalLabel)));
        }
    }

    private string _videoFlipVerticalLabel = string.Empty;
    public string VideoFlipVerticalLabel
    {
        get => _videoFlipVerticalLabel;
        set
        {
            _videoFlipVerticalLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VideoFlipVerticalLabel)));
        }
    }

    private string _audioDecoderLabel = string.Empty;
    public string AudioDecoderLabel
    {
        get => _audioDecoderLabel;
        set
        {
            _audioDecoderLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioDecoderLabel)));
        }
    }
    private string _audioRendererLabel = string.Empty;
    public string AudioRendererLabel
    {
        get => _audioRendererLabel;
        set
        {
            _audioRendererLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioRendererLabel)));
        }
    }

    private string _subtitleDecoderLabel = string.Empty;
    public string SubtitleDecoderLabel
    {
        get => _subtitleDecoderLabel;
        set
        {
            _subtitleDecoderLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubtitleDecoderLabel)));
        }
    }

    private string _subtitleRendererLabel = string.Empty;
    public string SubtitleRendererLabel
    {
        get => _subtitleRendererLabel;
        set
        {
            _subtitleRendererLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubtitleRendererLabel)));
        }
    }

    public PlayerSettingsPanelViewModel()
    {
        SettingsChangedLiveData = new(_settingsChangedLiveData, 500);
    }
}
