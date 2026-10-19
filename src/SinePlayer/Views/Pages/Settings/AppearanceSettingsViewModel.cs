// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.ComponentModel;

using SinePlayer.Common.Localization;
using SinePlayer.Data.Models.Misc;

using static SinePlayer.Views.Pages.Settings.SettingsPageViewModel;

namespace SinePlayer.Views.Pages.Settings;

internal partial class AppearanceSettingsViewModel : INotifyPropertyChanged
{
    private const string TAG = nameof(AppearanceSettingsViewModel);

    public event PropertyChangedEventHandler? PropertyChanged;

    private SettingsSharedViewModel _shared = new();
    public SettingsSharedViewModel Shared
    {
        get => _shared;
        private set
        {
            _shared = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Shared)));
        }
    }

    private List<BackgroundEntry> _backgrounds = [];
    public List<BackgroundEntry> Backgrounds
    {
        get => _backgrounds;
        set
        {
            _backgrounds = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Backgrounds)));
        }
    }

    private int _backgroundIndex = 0;
    public int BackgroundIndex
    {
        get => _backgroundIndex;
        set
        {
            _backgroundIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackgroundIndex)));
        }
    }

    private int _appearanceIndex;
    public int AppearanceIndex
    {
        get => _appearanceIndex;
        set
        {
            _appearanceIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppearanceIndex)));
        }
    }

    private bool _appearanceChanged;
    public bool AppearanceChanged
    {
        get => _appearanceChanged;
        set
        {
            _appearanceChanged = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppearanceChanged)));
        }
    }

    public void Initialize(SettingsSharedViewModel shared)
    {
        Shared = shared;
        Shared.UpdateStarted += Update;
    }

    private void Update()
    {
        UpdateAppearance();
        UpdateBackground();

        AppearanceChanged = false;
    }

    private void UpdateBackground()
    {
        AppSettingsModel.AppBackgroundEnum background = AppSettingsModel.AppBackground;
        List<BackgroundEntry> backgrounds = [
            new(StringResourceProvider.Instance.None, AppSettingsModel.AppBackgroundEnum.None),
            new(StringResourceProvider.Instance.BackgroundAcrylic, AppSettingsModel.AppBackgroundEnum.Acrylic),
            new("Mica", AppSettingsModel.AppBackgroundEnum.Mica),
            new("Mica Alt", AppSettingsModel.AppBackgroundEnum.MicaAlt),
        ];
        int backgroundIndex = backgrounds.FindIndex(x => x.Value == background);
        if (backgroundIndex < 0)
        {
            backgroundIndex = 0;
        }

        Backgrounds = backgrounds;
        BackgroundIndex = backgroundIndex;
    }

    private void UpdateAppearance()
    {
        AppSettingsModel.AppearanceSetting appearance = AppSettingsModel.Theme;
        if (!Enum.IsDefined(appearance))
        {
            appearance = AppSettingsModel.AppearanceSetting.UseSystemSetting;
        }

        AppearanceIndex = appearance switch
        {
            AppSettingsModel.AppearanceSetting.Light => 0,
            AppSettingsModel.AppearanceSetting.Dark => 1,
            AppSettingsModel.AppearanceSetting.UseSystemSetting => 2,
            _ => 2,
        };
    }
}
