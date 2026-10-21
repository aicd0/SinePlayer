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

    public PlayerSettingsPanelViewModel()
    {
        SettingsChangedLiveData = new(_settingsChangedLiveData, 500);
    }
}
