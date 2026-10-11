// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Common.BaseUI.PageAbilities;
using SinePlayerUWP.Common.Constants;
using SinePlayerUWP.Core.Common.Lifecycle;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Database;
using SinePlayerUWP.Data.Models.Media;
using SinePlayerUWP.Data.Models.Misc;
using SinePlayerUWP.Helpers.MenuFlyoutHelpers;
using SinePlayerUWP.Views.Dialogs.EditReaderSettingPreset;

namespace SinePlayerUWP.UserControls.ReaderSettings;

internal sealed partial class ReaderSettingsPanel : BaseUserControl
{
    private const string KEY_SELECTED_TAB = "SelectedReaderSettingsTab";

    public delegate void DataChangedEventHandler(ReaderSettingsModel data);
    public event DataChangedEventHandler? DataChanged;

    public ReaderSettingsPanelViewModel ViewModel { get; } = new();
    public bool ActionInProgress { get; private set; } = false;

    private IMainWindowAbility? _windowAbility;
    private MediaModel? _media;
    private ReaderSettingsModel _mediaSettings = ReaderSettingsModel.FromDefault();
    private ReaderSettingsModel? _presetSettings;
    private bool _updatingUI = false;

    public ReaderSettingsPanel()
    {
        InitializeComponent();

        string selectedTab = AppDB.MainRegistry.CreateKey(RegistryNames.SETTINGS).GetValueOrDefault(KEY_SELECTED_TAB, string.Empty);
        SettingsTabSelectorBar.SelectedItem = selectedTab switch
        {
            "ImageProcessing" => SelectorBarItem2,
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
            selectedTabKey = "General";
            GeneralSettingsGrid.Visibility = Visibility.Visible;
            ImageProcessingSettingsGrid.Visibility = Visibility.Collapsed;
        }
        else if (selectedItem == SelectorBarItem2)
        {
            selectedTabKey = "ImageProcessing";
            GeneralSettingsGrid.Visibility = Visibility.Collapsed;
            ImageProcessingSettingsGrid.Visibility = Visibility.Visible;
        }

        AppDB.MainRegistry.CreateKey(RegistryNames.SETTINGS).Set(KEY_SELECTED_TAB, selectedTabKey);
    }

    private void TwoPageModeToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        PageLayoutSettings pageLayoutSettings = _mediaSettings.IsVertical ? _mediaSettings.VerticalPageLayout : _mediaSettings.HorizontalPageLayout;
        pageLayoutSettings.TwoPageMode = ((ToggleSwitch)sender).IsOn;
        NotifySettingsChanged();
    }

    private void CoverPageCountSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        PageLayoutSettings pageLayoutSettings = _mediaSettings.IsVertical ? _mediaSettings.VerticalPageLayout : _mediaSettings.HorizontalPageLayout;
        pageLayoutSettings.CoverPageCount = Math.Clamp((int)e.NewValue, 0, 2);
        NotifySettingsChanged();
    }

    private void SwapLeftAndRightPagesToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        PageLayoutSettings pageLayoutSettings = _mediaSettings.IsVertical ? _mediaSettings.VerticalPageLayout : _mediaSettings.HorizontalPageLayout;
        pageLayoutSettings.SwapLeftAndRightPages = ((ToggleSwitch)sender).IsOn;
        NotifySettingsChanged();
    }

    private void SpreadDetectionToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        PageLayoutSettings pageLayoutSettings = _mediaSettings.IsVertical ? _mediaSettings.VerticalPageLayout : _mediaSettings.HorizontalPageLayout;
        pageLayoutSettings.SpreadDetection = ((ToggleSwitch)sender).IsOn;
        NotifySettingsChanged();
    }

    private void AbbOrientation_Click(object sender, RoutedEventArgs e)
    {
        _mediaSettings.IsVertical = !_mediaSettings.IsVertical;
        NotifySettingsChanged();
    }

    private void AbbFlowDirection_Click(object sender, RoutedEventArgs e)
    {
        _mediaSettings.IsLeftToRight = !_mediaSettings.IsLeftToRight;
        NotifySettingsChanged();
    }

    private void AbbContinuous_Click(object sender, RoutedEventArgs e)
    {
        _mediaSettings.IsContinuous = !_mediaSettings.IsContinuous;
        NotifySettingsChanged();
    }

    private void PageSpacingSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _mediaSettings.PageSpacing = Math.Clamp((int)e.NewValue, 0, 200);
        NotifySettingsChanged();
    }

    private void AutoScrollingSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _mediaSettings.AutoScrollSpeed = Math.Clamp((int)e.NewValue, 0, 100);
        NotifySettingsChanged();
    }

    private void OriginalSizeToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        _mediaSettings.OriginalSize = OriginalSizeToggleSwitch.IsOn;
        NotifySettingsChanged();
    }

    private void FlipImageToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        _mediaSettings.ImageFlip = FlipImageToggleSwitch.IsOn;
        NotifySettingsChanged();
    }

    private void AntiAliasingFilterSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _mediaSettings.AntiAliasingFilterPercentage = Math.Clamp((int)e.NewValue, 0, 200);
        NotifySettingsChanged();
    }

    private void BrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _mediaSettings.BrightnessPercentage = Math.Clamp((int)e.NewValue, 0, 100);
        NotifySettingsChanged();
    }

    private void ContrastSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _mediaSettings.ContrastPercentage = Math.Clamp((int)e.NewValue, 0, 100);
        NotifySettingsChanged();
    }

    private void SaturationSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _mediaSettings.SaturationPercentage = Math.Clamp((int)e.NewValue, 0, 100);
        NotifySettingsChanged();
    }

    private void InvertImageToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        _mediaSettings.ImageInvert = InvertImageToggleSwitch.IsOn;
        NotifySettingsChanged();
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
            var dialog = new EditReaderSettingPresetDialog(_media);
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
        var newSettings = ReaderSettingsModel.FromDefault();
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

        _mediaSettings = ReaderSettingsModel.LoadFromMedia(_media);
        _presetSettings = ReaderSettingsModel.LoadFromPreset(_mediaSettings.PresetKey);
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
        string GetLabel(string name, bool modified)
        {
            return modified ? name + " *" : name;
        }

        ReaderSettingsModel mediaSettings = _mediaSettings;
        PageLayoutSettings mediaLayoutSettings = mediaSettings.IsVertical ? mediaSettings.VerticalPageLayout : mediaSettings.HorizontalPageLayout;
        ReaderSettingsModel presetSettings = _presetSettings ?? mediaSettings;
        PageLayoutSettings presetLayoutSettings = mediaSettings.IsVertical ? presetSettings.VerticalPageLayout : presetSettings.HorizontalPageLayout;

        bool settingsModified = false;

        {
            bool tabModified = false;

            {
                bool modified = mediaLayoutSettings.TwoPageMode != presetLayoutSettings.TwoPageMode;
                tabModified = tabModified || modified;
                TwoPageModeToggleSwitch.IsOn = mediaLayoutSettings.TwoPageMode;
                ViewModel.TwoPageModeLabel = GetLabel(StringResource.TwoPageMode, modified);
            }

            {
                bool modified = mediaLayoutSettings.CoverPageCount != presetLayoutSettings.CoverPageCount;
                tabModified = tabModified || modified;
                CoverPageCountSlider.Value = Math.Clamp(mediaLayoutSettings.CoverPageCount, 0, 2);
                CoverPageCountSlider.DefaultValue = presetLayoutSettings.CoverPageCount;
                CoverPageCountSlider.Visibility = mediaLayoutSettings.TwoPageMode ? Visibility.Visible : Visibility.Collapsed;
                ViewModel.CoverPageCountLabel = GetLabel(StringResource.CoverPageCount, modified);
            }

            {
                bool modified = mediaLayoutSettings.SwapLeftAndRightPages != presetLayoutSettings.SwapLeftAndRightPages;
                tabModified = tabModified || modified;
                SwapLeftAndRightPagesToggleSwitch.IsOn = mediaLayoutSettings.SwapLeftAndRightPages;
                SwapLeftAndRightPagesGrid.Visibility = mediaLayoutSettings.TwoPageMode ? Visibility.Visible : Visibility.Collapsed;
                ViewModel.SwapLeftAndRightPagesLabel = GetLabel(StringResource.SwapLeftAndRightPages, modified);
            }

            {
                bool modified = mediaLayoutSettings.SpreadDetection != presetLayoutSettings.SpreadDetection;
                tabModified = tabModified || modified;
                SpreadDetectionToggleSwitch.IsOn = mediaLayoutSettings.SpreadDetection;
                ViewModel.SpreadDetectionLabel = GetLabel(StringResource.SpreadDetection, modified);
            }

            {
                bool modified = mediaSettings.IsVertical != presetSettings.IsVertical;
                tabModified = tabModified || modified;
                AbbOrientation.Label = GetLabel(
                    mediaSettings.IsVertical ? StringResource.Vertical : StringResource.Horizontal,
                    modified);
                AbbOrientation.Content = new FontIcon() { Glyph = mediaSettings.IsVertical ? "\uE7C3" : "\uEF6B" };
            }

            {
                bool modified = mediaSettings.IsLeftToRight != presetSettings.IsLeftToRight;
                tabModified = tabModified || modified;
                AbbFlowDirection.Label = GetLabel(
                    mediaSettings.IsLeftToRight ? StringResource.LeftToRight : StringResource.RightToLeft,
                    modified);
                AbbFlowDirection.Content = new FontIcon() { Glyph = mediaSettings.IsLeftToRight ? "\uEBE7" : "\uEC52" };
            }

            {
                bool modified = mediaSettings.IsContinuous != presetSettings.IsContinuous;
                tabModified = tabModified || modified;
                AbbContinuous.Label = GetLabel(
                    mediaSettings.IsContinuous ? StringResource.Continuous : StringResource.Separate,
                    modified);
                AbbContinuous.Content = new FontIcon() { Glyph = mediaSettings.IsContinuous ? "\uE785" : "\uE72E" };
            }

            {
                bool modified = mediaSettings.PageSpacing != presetSettings.PageSpacing;
                tabModified = tabModified || modified;
                PageSpacingSlider.Value = Math.Clamp(mediaSettings.PageSpacing, 0, 200);
                PageSpacingSlider.DefaultValue = presetSettings.PageSpacing;
                ViewModel.PageSpacingLabel = GetLabel(StringResource.PageSpacing, modified);
            }

            {
                bool modified = mediaSettings.AutoScrollSpeed != presetSettings.AutoScrollSpeed;
                tabModified = tabModified || modified;
                AutoScrollingSlider.Value = Math.Clamp(mediaSettings.AutoScrollSpeed, 0, 100);
                AutoScrollingSlider.DefaultValue = presetSettings.AutoScrollSpeed;
                ViewModel.AutoScrollingLabel = GetLabel(StringResource.AutoScrolling, modified);
            }

            {
                bool modified = mediaSettings.OriginalSize != presetSettings.OriginalSize;
                tabModified = tabModified || modified;
                OriginalSizeToggleSwitch.IsOn = mediaSettings.OriginalSize;
                ViewModel.OriginalSizeLabel = GetLabel(StringResource.MaintainRelativeSize, modified);
            }

            settingsModified = settingsModified || tabModified;
            ViewModel.GeneralTabTitle = GetLabel(StringResource.General, tabModified);
        }

        {
            bool tabModified = false;

            {
                bool modified = mediaSettings.ImageRotation != presetSettings.ImageRotation;
                tabModified = tabModified || modified;
                UpdateImageRotation(mediaSettings);
                ViewModel.RotationLabel = GetLabel(StringResource.Rotation, modified);
            }

            {
                bool modified = mediaSettings.ImageFlip != presetSettings.ImageFlip;
                tabModified = tabModified || modified;
                FlipImageToggleSwitch.IsOn = mediaSettings.ImageFlip;
                ViewModel.FlipImageLabel = GetLabel(StringResource.FlipImage, modified);
            }

            {
                bool modified = mediaSettings.AntiAliasingFilterPercentage != presetSettings.AntiAliasingFilterPercentage;
                tabModified = tabModified || modified;
                AntiAliasingFilterSlider.Value = Math.Clamp(mediaSettings.AntiAliasingFilterPercentage, 0, 200);
                AntiAliasingFilterSlider.DefaultValue = presetSettings.AntiAliasingFilterPercentage;
                ViewModel.AntiAliasingFilterLabel = GetLabel(StringResource.AntiAliasingFilter, modified);
            }

            {
                bool modified = mediaSettings.BrightnessPercentage != presetSettings.BrightnessPercentage;
                tabModified = tabModified || modified;
                BrightnessSlider.Value = Math.Clamp(mediaSettings.BrightnessPercentage, 0, 100);
                BrightnessSlider.DefaultValue = presetSettings.BrightnessPercentage;
                ViewModel.BrightnessLabel = GetLabel(StringResource.ImageBrightness, modified);
            }

            {
                bool modified = mediaSettings.ContrastPercentage != presetSettings.ContrastPercentage;
                tabModified = tabModified || modified;
                ContrastSlider.Value = Math.Clamp(mediaSettings.ContrastPercentage, 0, 100);
                ContrastSlider.DefaultValue = presetSettings.ContrastPercentage;
                ViewModel.ContrastLabel = GetLabel(StringResource.ImageContrast, modified);
            }

            {
                bool modified = mediaSettings.SaturationPercentage != presetSettings.SaturationPercentage;
                tabModified = tabModified || modified;
                SaturationSlider.Value = Math.Clamp(mediaSettings.SaturationPercentage, 0, 100);
                SaturationSlider.DefaultValue = presetSettings.SaturationPercentage;
                ViewModel.SaturationLabel = GetLabel(StringResource.ImageSaturation, modified);
            }

            {
                bool modified = mediaSettings.ImageInvert != presetSettings.ImageInvert;
                tabModified = tabModified || modified;
                InvertImageToggleSwitch.IsOn = mediaSettings.ImageInvert;
                ViewModel.InvertImageLabel = GetLabel(StringResource.InvertImage, modified);
            }

            settingsModified = settingsModified || tabModified;
            ViewModel.ImageProcessingTabTitle = GetLabel(StringResource.Image, tabModified);
        }

        PresetDropDownButton.Flyout = CreatePresetContextMenu(mediaSettings);
        PresetDropDownButton.Content = GetLabel(
            mediaSettings.PresetKey == ReaderSettingsModel.PRESET_KEY_CUSTOM ?
                StringResource.Custom : mediaSettings.PresetName,
            settingsModified);
    }

    private void UpdateImageRotation(ReaderSettingsModel mediaSettings)
    {
        List<Tuple<string, ImageRotationEnum>> rotations =
        [
            new(StringResource.None, ImageRotationEnum.None),
            new("90º", ImageRotationEnum.Rotate90),
            new("180º", ImageRotationEnum.Rotate180),
            new("270º", ImageRotationEnum.Rotate270),
        ];

        List<BaseMenuFlyoutItemModel> items = [];
        foreach (Tuple<string, ImageRotationEnum> rotation in rotations)
        {
            ImageRotationEnum rotationValue = rotation.Item2;
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = rotation.Item1,
                IsChecked = mediaSettings.ImageRotation == rotationValue,
                Click = () =>
                {
                    if (mediaSettings.ImageRotation != rotationValue)
                    {
                        mediaSettings.ImageRotation = rotationValue;
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

        RotationDropDownButton.Flyout = flyout;
        RotationDropDownButton.Content = rotations.Find(r => r.Item2 == mediaSettings.ImageRotation)?.Item1 ?? StringResource.None;
    }

    private MenuFlyout CreatePresetContextMenu(ReaderSettingsModel mediaSettings)
    {
        List<Tuple<string, string>> presets = [];
        foreach (KeyValuePair<string, ReaderSettingsModel> kvp in AppSettingsModel.ReaderSettingPresets)
        {
            presets.Add(new Tuple<string, string>(kvp.Value.PresetName, kvp.Key));
        }

        if (presets.Count == 0)
        {
            presets.Add(new Tuple<string, string>(StringResource.Default, ReaderSettingsModel.PRESET_KEY_DEFAULT));
        }

        presets.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Item1, b.Item1));
        presets.Add(new Tuple<string, string>(StringResource.Custom, ReaderSettingsModel.PRESET_KEY_CUSTOM));

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
                    var newSettings = ReaderSettingsModel.LoadFromPreset(presetKey);
                    if (newSettings is not null)
                    {
                        _presetSettings = newSettings;
                        _mediaSettings = newSettings.Clone();
                        SaveSettings();
                        DispatchDataChangeEvent();
                    }
                    else if (presetKey == ReaderSettingsModel.PRESET_KEY_CUSTOM)
                    {
                        _presetSettings = null;
                        _mediaSettings.PresetKey = presetKey;
                        SaveSettings();
                    }
                    else
                    {
                        newSettings = ReaderSettingsModel.FromDefault();
                        _presetSettings = newSettings;
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
