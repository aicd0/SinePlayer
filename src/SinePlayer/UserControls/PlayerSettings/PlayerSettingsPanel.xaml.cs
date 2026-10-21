// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Constants;
using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Database;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Views.Dialogs.EditPlayerSettingPreset;

namespace SinePlayer.UserControls.PlayerSettings;

internal sealed partial class PlayerSettingsPanel : BaseUserControl
{
    public delegate void DataChangedEventHandler(PlayerSettingsModel data);
    public event DataChangedEventHandler? DataChanged;

    public PlayerSettingsPanelViewModel ViewModel { get; } = new();
    public bool ActionInProgress { get; private set; } = false;

    private IMainWindowAbility? _windowAbility;
    private MediaModel? _media;
    private PlayerSettingsModel _mediaSettings = PlayerSettingsModel.FromDefault();
    private bool _updatingUI = false;

    public PlayerSettingsPanel()
    {
        InitializeComponent();

        string selectedTab = AppDB.MainRegistry.CreateKey(RegistryNames.SETTINGS).GetValueOrDefault(RegistryNames.StateKey.SELECTED_PLAYER_SETTINGS_TAB, string.Empty);
        SettingsTabSelectorBar.SelectedItem = selectedTab switch
        {
            "Second" => SelectorBarItem2,
            _ => SelectorBarItem1,
        };
    }

    public void Initialize(IMainWindowAbility windowAbility)
    {
        _windowAbility = windowAbility;
    }

    public void SetMedia(MediaModel media)
    {
        if (_media == media)
        {
            return;
        }

        _media = media;
        LoadSettings();
        UpdateUI();
        DispatchDataChangeEvent();
    }

    protected override void OnResume()
    {
        base.OnResume();
        ObserveData();
    }

    private void ObserveData()
    {
        ObserveOptions options = new()
        {
            PublishBehavior = LiveDataPublishBehavior.ResumeOnly,
        };

        ViewModel.SettingsChangedLiveData.Observe(this, _ =>
        {
            SaveSettings();
            UpdateUI();
            DispatchDataChangeEvent();
        }, options);
    }

    private void SelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        string selectedTabKey = string.Empty;
        SelectorBarItem selectedItem = sender.SelectedItem;
        if (selectedItem == SelectorBarItem1)
        {
            selectedTabKey = "Video";
            VideoSettingsGrid.Visibility = Visibility.Visible;
            AudioSettingsGrid.Visibility = Visibility.Collapsed;
        }
        else if (selectedItem == SelectorBarItem2)
        {
            selectedTabKey = "Audio";
            AudioSettingsGrid.Visibility = Visibility.Visible;
            VideoSettingsGrid.Visibility = Visibility.Collapsed;
        }

        AppDB.MainRegistry.CreateKey(RegistryNames.SETTINGS).Set(RegistryNames.StateKey.SELECTED_PLAYER_SETTINGS_TAB, selectedTabKey);
    }

    private async void EditPresetButton_Click(object sender, RoutedEventArgs e)
    {
        IMainWindowAbility? windowAbility = _windowAbility;
        if (_media is null || windowAbility is null)
        {
            return;
        }

        ActionInProgress = true;
        try
        {
            var dialog = new EditPlayerSettingPresetDialog(_media);
            await dialog.ShowAsync(windowAbility.WindowId);
        }
        finally
        {
            ActionInProgress = false;
        }

        LoadSettings();
        UpdateUI();
        DispatchDataChangeEvent();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var newSettings = PlayerSettingsModel.FromDefault();
        newSettings.PresetKey = _mediaSettings.PresetKey;
        newSettings.PresetName = _mediaSettings.PresetName;
        _mediaSettings = newSettings;
        NotifySettingsChanged();
    }

    private void LoadSettings()
    {
        if (_media is null)
        {
            return;
        }

        _mediaSettings = PlayerSettingsModel.LoadFromMedia(_media);
    }

    private void NotifySettingsChanged()
    {
        ViewModel.SettingsChangedLiveData.Emit(true);
    }

    private void SaveSettings()
    {
        if (_updatingUI || _media is null)
        {
            return;
        }

        _mediaSettings.SaveToMedia(_media);
    }

    private void UpdateUI()
    {
        if (_updatingUI)
        {
            return;
        }

        _updatingUI = true;
        try
        {
            UpdateUIInternal();
        }
        finally
        {
            _updatingUI = false;
        }
    }

    private void UpdateUIInternal()
    {
        ViewModel.VideoTabTitle = StringResource.Video;
        ViewModel.AudioTabTitle = StringResource.Audio;

        PresetDropDownButton.Flyout = CreatePresetContextMenu(_mediaSettings);
        PresetDropDownButton.Content = _mediaSettings.PresetKey == PlayerSettingsModel.PRESET_KEY_CUSTOM ?
            StringResourceProvider.Instance.Custom : _mediaSettings.PresetName;
    }

    private MenuFlyout CreatePresetContextMenu(PlayerSettingsModel mediaSettings)
    {
        List<Tuple<string, string>> presets = [];
        foreach (KeyValuePair<string, PlayerSettingsModel> kvp in AppSettingsModel.PlayerSettingPresets)
        {
            presets.Add(new Tuple<string, string>(kvp.Value.PresetName, kvp.Key));
        }

        if (presets.Count == 0)
        {
            presets.Add(new Tuple<string, string>(StringResource.Default, PlayerSettingsModel.PRESET_KEY_DEFAULT));
        }

        presets.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Item1, b.Item1));
        presets.Add(new Tuple<string, string>(StringResource.Custom, PlayerSettingsModel.PRESET_KEY_CUSTOM));

        List<BaseMenuFlyoutItemModel> items = [];
        foreach (Tuple<string, string> preset in presets)
        {
            string presetKey = preset.Item2;
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = preset.Item1,
                IsChecked = mediaSettings.PresetKey == presetKey,
                Click = () =>
                {
                    var newSettings = PlayerSettingsModel.LoadFromPreset(presetKey);
                    if (newSettings is not null)
                    {
                        _mediaSettings = newSettings.Clone();
                        SaveSettings();
                        DispatchDataChangeEvent();
                    }
                    else if (presetKey == PlayerSettingsModel.PRESET_KEY_CUSTOM)
                    {
                        _mediaSettings.PresetKey = presetKey;
                        SaveSettings();
                    }
                    else
                    {
                        newSettings = PlayerSettingsModel.FromDefault();
                        _mediaSettings = newSettings.Clone();
                        SaveSettings();
                        DispatchDataChangeEvent();
                    }

                    UpdateUI();
                },
            });
        }

        var flyout = new MenuFlyout();
        foreach (BaseMenuFlyoutItemModel item in items)
        {
            flyout.Items.Add(item.CreateMenuFlyoutItem());
        }

        return flyout;
    }

    private void DispatchDataChangeEvent()
    {
        if (_updatingUI)
        {
            return;
        }

        DataChanged?.Invoke(_mediaSettings);
    }
}
