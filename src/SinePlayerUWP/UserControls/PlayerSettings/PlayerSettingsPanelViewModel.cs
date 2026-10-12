// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;

using SinePlayerUWP.Core.Common.Lifecycle;
using SinePlayerUWP.Core.Common.Lifecycle.Utils;

namespace SinePlayerUWP.UserControls.PlayerSettings;

internal partial class PlayerSettingsPanelViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly MutableLiveData<bool> _settingsChangedLiveData = new();
    public readonly MutableLiveDataWithDelay<bool> SettingsChangedLiveData;

    private string _generalTabTitle = string.Empty;
    public string GeneralTabTitle
    {
        get => _generalTabTitle;
        set
        {
            _generalTabTitle = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GeneralTabTitle)));
        }
    }

    private string _secondTabTitle = string.Empty;
    public string SecondTabTitle
    {
        get => _secondTabTitle;
        set
        {
            _secondTabTitle = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SecondTabTitle)));
        }
    }

    public PlayerSettingsPanelViewModel()
    {
        SettingsChangedLiveData = new(_settingsChangedLiveData, 500);
    }
}
