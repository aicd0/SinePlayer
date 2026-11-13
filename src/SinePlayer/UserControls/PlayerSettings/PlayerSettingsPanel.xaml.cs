// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Globalization;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Constants;
using SinePlayer.Common.Localization;
using SinePlayer.Common.Playback.Core;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.Win2D;
using SinePlayer.Data.Database;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Helpers.Misc;
using SinePlayer.Views.Dialogs.EditPlayerSettingPreset;

using Windows.UI;
using Windows.UI.Text;

namespace SinePlayer.UserControls.PlayerSettings;

internal sealed partial class PlayerSettingsPanel : BaseUserControl
{
    #region Constructor

    private const char SectionPathSeparator = '/';
    private const char CollapsedSectionsSeparator = ';';

    private const double MinimumSubtitleFontSize = 4.0;
    private const double MaximumSubtitleFontSize = 200.0;
    private const int DefaultSubtitleFontWeight = 400;

    private static readonly double[] SubtitleFontSizePresets = [16.0, 20.0, 24.0, 28.0, 32.0, 36.0, 40.0, 48.0, 56.0, 64.0, 72.0];

    public delegate void DataChangedEventHandler();
    public event DataChangedEventHandler? DataChanged;

    public PlayerSettingsPanelViewModel ViewModel { get; } = new();
    public bool ActionInProgress { get; private set; } = false;

    private IMainWindowAbility? _windowAbility;
    private MediaModel? _media;
    private PlayerSession? _session;
    private readonly PlayerSettingsModel _mediaSettings = new();
    private PlayerSettingsModel? _presetSettings;

    public PlayerSettingsPanel()
    {
        InitializeComponent();

        SettingsCategory video = new()
        {
            Get = settings => settings.Video,
            Set = (settings, value) =>
            {
                if (value is VideoSettingsModel videoSettings)
                {
                    settings.Video = videoSettings;
                }
            },
        };
        SettingsCategory audio = new()
        {
            Get = settings => settings.Audio,
            Set = (settings, value) =>
            {
                if (value is AudioSettingsModel audioSettings)
                {
                    settings.Audio = audioSettings;
                }
            },
        };
        SettingsCategory subtitle = new()
        {
            Get = settings => settings.Subtitle,
            Set = (settings, value) =>
            {
                if (value is SubtitleSettingsModel subtitleSettings)
                {
                    settings.Subtitle = subtitleSettings;
                }
            },
        };

        _tabs.Add(_videoTab = new() { Key = "Video", Item = SelectorBarItem1, Content = VideoSettingsGrid, Category = video });
        _tabs.Add(_audioTab = new() { Key = "Audio", Item = SelectorBarItem2, Content = AudioSettingsGrid, Category = audio });
        _tabs.Add(_subtitleTab = new() { Key = "Subtitle", Item = SelectorBarItem3, Content = SubtitleSettingsGrid, Category = subtitle });
        _activeTab = _videoTab;

        Loaded += PlayerSettingsPanel_LoadedOrUnloaded;
        Unloaded += PlayerSettingsPanel_LoadedOrUnloaded;

        string selectedTab = AppDB.MainRegistry.CreateKey(RegistryNames.STATE).GetValueOrDefault(RegistryNames.StateKey.SELECTED_PLAYER_SETTINGS_TAB, string.Empty);
        SettingsTab? tab = _tabs.Find(candidate => string.Equals(candidate.Key, selectedTab, StringComparison.Ordinal));
        SettingsTabSelectorBar.SelectedItem = (tab ?? _videoTab).Item;

        RestoreSectionStates();
    }

    #endregion

    #region Public API

    public PlayerSettingsModel Settings => _mediaSettings;

    public void Initialize(IMainWindowAbility windowAbility)
    {
        _windowAbility = windowAbility;
    }

    public void SetMedia(MediaModel media, PlayerSession? session)
    {
        AttachSession(session);

        if (_media != media)
        {
            _media = media;
            LoadTrackScopes();
            RefreshScopeComboBox();
            LoadSettings();
            UpdateUI();
            DispatchDataChangeEvent();
        }
    }

    public void Apply(Action<PlayerSettingsModel> change)
    {
        change(_mediaSettings);
        SaveSettings();
        DispatchDataChangeEvent();
        UpdateUI();
    }

    public bool NavigateTo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string[] segments = path.Split(SectionPathSeparator);
        SettingsTab? tab = _tabs.Find(candidate => string.Equals(candidate.Key, segments[0], StringComparison.Ordinal));
        if (tab is null)
        {
            return false;
        }

        StackPanel tabPanel = tab.Content;
        List<PlayerSettingsSection> chain = [];
        IEnumerable<object> container = tabPanel.Children;
        for (int i = 1; i < segments.Length; i++)
        {
            PlayerSettingsSection? found = null;
            foreach (object child in container)
            {
                if (child is PlayerSettingsSection section && string.Equals(section.SectionKey, segments[i], StringComparison.Ordinal))
                {
                    found = section;
                    break;
                }
            }

            if (found is null)
            {
                return false;
            }

            chain.Add(found);
            container = found.SectionChildren;
        }

        SettingsTabSelectorBar.SelectedItem = tab.Item;

        if (chain.Count == 0)
        {
            return true;
        }

        foreach (PlayerSettingsSection section in chain)
        {
            section.IsExpanded = true;
        }

        PlayerSettingsSection target = chain[^1];
        DispatcherQueue.TryEnqueue(() => target.StartBringIntoView(new BringIntoViewOptions
        {
            VerticalAlignmentRatio = 0.0,
        }));

        return true;
    }

    #endregion

    #region Lifecycle

    protected override void OnResume()
    {
        base.OnResume();
        ObserveData();
    }

    private void PlayerSettingsPanel_LoadedOrUnloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            AttachSession(_session);
            RefreshTracks();
        }
        else
        {
            DetachSessionEvents();
        }
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

    #endregion

    #region Tab UI

    private readonly List<SettingsTab> _tabs = [];
    private readonly SettingsTab _videoTab;
    private readonly SettingsTab _audioTab;
    private readonly SettingsTab _subtitleTab;
    private SettingsTab _activeTab;

    private void SelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        SettingsTab? selectedTab = _tabs.Find(candidate => candidate.Item == sender.SelectedItem);
        if (selectedTab is null)
        {
            return;
        }

        _activeTab = selectedTab;

        foreach (SettingsTab tab in _tabs)
        {
            tab.Content.Visibility = tab == selectedTab ? Visibility.Visible : Visibility.Collapsed;
        }

        AppDB.MainRegistry.CreateKey(RegistryNames.STATE).Set(RegistryNames.StateKey.SELECTED_PLAYER_SETTINGS_TAB, selectedTab.Key);

        if (_media is not null)
        {
            RefreshScopeComboBox();
            UpdateUI();
        }
    }

    #endregion

    #region Preset UI

    private async void EditPresetButton_Click(object sender, RoutedEventArgs e)
    {
        IMainWindowAbility? windowAbility = _windowAbility;
        if (_media is not { } media || windowAbility is null)
        {
            return;
        }

        PresetSettingsModel settings = ActiveSettings();
        string presetKey = settings.PresetKey;

        ActionInProgress = true;
        try
        {
            var dialog = new EditPlayerSettingPresetDialog(settings);
            await dialog.ShowAsync(windowAbility.WindowId);
        }
        finally
        {
            ActionInProgress = false;
        }

        if (settings.PresetKey != presetKey)
        {
            if (KindOf(_activeTab) is { } kind)
            {
                SaveCategory(media, kind, TrackOf(kind), settings);
            }
        }

        LoadSettings();
        UpdateUI();
        DispatchDataChangeEvent();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsCategory category = _activeTab.Category;
        PresetSettingsModel current = category.Get(_mediaSettings);
        category.Set(_mediaSettings, KeepPreset(current, current.CreateDefault()));

        if (_media is { } media && KindOf(_activeTab) is { } kind && TrackOf(kind) is null)
        {
            // Reset under Global means the media goes back to one set of values for every track.
            PlayerSettingsStore.ClearTrackRecords(media, kind);
        }

        NotifySettingsChanged();
    }

    private MenuFlyout CreatePresetContextMenu()
    {
        List<Tuple<string, string>> presets = [];
        foreach ((string key, string name) in ActiveSettings().ListPresets())
        {
            presets.Add(new Tuple<string, string>(name, key));
        }

        if (presets.Count == 0)
        {
            presets.Add(new Tuple<string, string>(StringResource.Default, PresetSettingsModel.PRESET_KEY_DEFAULT));
        }

        presets.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Item1, b.Item1));
        presets.Add(new Tuple<string, string>(StringResource.Custom, PresetSettingsModel.PRESET_KEY_CUSTOM));

        List<BaseMenuFlyoutItemModel> items = [];
        foreach (Tuple<string, string> preset in presets)
        {
            string presetKey = preset.Item2;
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = preset.Item1,
                IsChecked = ActiveSettings().PresetKey == presetKey,
                Click = () => ApplyPreset(presetKey),
            });
        }

        var flyout = new MenuFlyout();
        foreach (BaseMenuFlyoutItemModel item in items)
        {
            flyout.Items.Add(item.CreateMenuFlyoutItem());
        }

        return flyout;
    }

    private void ApplyPreset(string presetKey)
    {
        SettingsCategory category = _activeTab.Category;
        PresetSettingsModel current = category.Get(_mediaSettings);

        PresetSettingsModel settings;
        if (presetKey == PresetSettingsModel.PRESET_KEY_CUSTOM)
        {
            current.PresetKey = presetKey;
            current.PresetName = StringResourceProvider.Instance.Custom;
            settings = current;
        }
        else
        {
            settings = current.FindPreset(presetKey) ?? WithPresetKey(current.CreateDefault(), presetKey);
        }

        category.Set(_mediaSettings, settings);
        SaveSettings();
        DispatchDataChangeEvent();
        UpdateUI();
    }

    #endregion

    #region Section UI

    private void RestoreSectionStates()
    {
        string collapsed = AppDB.MainRegistry.CreateKey(RegistryNames.SETTINGS).GetValueOrDefault(RegistryNames.StateKey.PLAYER_SETTINGS_COLLAPSED_SECTIONS, string.Empty);
        HashSet<string> collapsedPaths = [.. collapsed.Split(CollapsedSectionsSeparator, StringSplitOptions.RemoveEmptyEntries)];

        foreach ((string sectionPath, PlayerSettingsSection section) in EnumerateSections())
        {
            section.IsExpanded = !collapsedPaths.Contains(sectionPath);
            section.ExpandedChanged += Section_ExpandedChanged;
        }
    }

    private void Section_ExpandedChanged(PlayerSettingsSection section, bool isExpanded)
    {
        List<string> collapsed = [];
        foreach ((string sectionPath, PlayerSettingsSection candidate) in EnumerateSections())
        {
            if (!candidate.IsExpanded)
            {
                collapsed.Add(sectionPath);
            }
        }

        AppDB.MainRegistry.CreateKey(RegistryNames.SETTINGS).Set(RegistryNames.StateKey.PLAYER_SETTINGS_COLLAPSED_SECTIONS, string.Join(CollapsedSectionsSeparator, collapsed));
    }

    private IEnumerable<(string Path, PlayerSettingsSection Section)> EnumerateSections()
    {
        foreach ((string Tab, StackPanel Panel) tab in (ValueTuple<string, StackPanel>[])[
            ("Video", VideoSettingsGrid),
            ("Audio", AudioSettingsGrid),
            ("Subtitle", SubtitleSettingsGrid)])
        {
            foreach ((string Path, PlayerSettingsSection Section) item in EnumerateSections(tab.Panel.Children))
            {
                yield return ($"{tab.Tab}{SectionPathSeparator}{item.Path}", item.Section);
            }
        }
    }

    private static IEnumerable<(string Path, PlayerSettingsSection Section)> EnumerateSections(IEnumerable<object> children)
    {
        foreach (object child in children)
        {
            if (child is not PlayerSettingsSection section || string.IsNullOrEmpty(section.SectionKey))
            {
                continue;
            }

            yield return (section.SectionKey, section);

            foreach ((string Path, PlayerSettingsSection Section) nested in EnumerateSections(section.SectionChildren))
            {
                yield return ($"{section.SectionKey}{SectionPathSeparator}{nested.Path}", nested.Section);
            }
        }
    }

    #endregion

    #region Track selection UI

    private readonly Dictionary<MediaStreamKind, TrackIdentityModel> _trackScopes = [];

    private MediaStreamKind? KindOf(SettingsTab tab)
    {
        if (tab == _videoTab)
        {
            return MediaStreamKind.Video;
        }

        if (tab == _audioTab)
        {
            return MediaStreamKind.Audio;
        }

        if (tab == _subtitleTab)
        {
            return MediaStreamKind.Subtitle;
        }

        // Other tabs are not configured per media stream.
        return null;
    }

    private TrackIdentityModel? TrackOf(MediaStreamKind kind)
    {
        return _trackScopes.TryGetValue(kind, out TrackIdentityModel? track) ? track : null;
    }

    private void LoadTrackScopes()
    {
        _trackScopes.Clear();
        if (_media is not { } media)
        {
            return;
        }

        foreach (SettingsTab tab in _tabs)
        {
            if (KindOf(tab) is { } kind && PlayerSettingsStore.GetTrackScope(media, kind) is { } track)
            {
                _trackScopes[kind] = track;
            }
        }
    }

    private void SetTrack(MediaStreamKind kind, TrackIdentityModel? track)
    {
        if (track is null)
        {
            _trackScopes.Remove(kind);
        }
        else
        {
            _trackScopes[kind] = track;
        }

        if (_media is { } media)
        {
            PlayerSettingsStore.SetTrackScope(media, kind, track);
        }
    }

    private void RefreshScopeComboBox()
    {
        _updatingUI = true;
        try
        {
            TrackScopeComboBox.Items.Clear();
            if (KindOf(_activeTab) is not { } kind)
            {
                return;
            }

            TrackIdentityModel? selected = TrackOf(kind);
            bool exists = selected is null;

            TrackScopeComboBox.Items.Add(new ComboBoxItem { Content = StringResourceProvider.Instance.Global, Tag = null });

            IReadOnlyList<(MediaSource Source, MediaStreamInfo Stream)> tracks = _session?.SelectedTracks(kind) ?? [];
            foreach ((MediaSource source, MediaStreamInfo stream) in tracks)
            {
                var track = TrackIdentityModel.From(stream, source.Locator.Value);
                TrackScopeComboBox.Items.Add(new ComboBoxItem { Content = MediaStreamLabel.Describe(stream, source), Tag = track });
                exists = exists || track.Equals(selected);
            }

            if (!exists && tracks.Count > 0)
            {
                SetTrack(kind, null);
                selected = null;
            }

            TrackScopeComboBox.SelectedIndex = IndexOfScope(selected);
        }
        finally
        {
            _updatingUI = false;
        }
    }

    private int IndexOfScope(TrackIdentityModel? track)
    {
        for (int i = 0; i < TrackScopeComboBox.Items.Count; i++)
        {
            if (TrackScopeComboBox.Items[i] is ComboBoxItem item && Equals(item.Tag, track))
            {
                return i;
            }
        }

        return 0;
    }

    private void ScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUI)
        {
            return;
        }

        if (sender is not ComboBox comboBox || KindOf(_activeTab) is not { } kind)
        {
            return;
        }

        SetTrack(kind, (comboBox.SelectedItem as ComboBoxItem)?.Tag as TrackIdentityModel);
        LoadSettings();
        UpdateUI();
    }

    #endregion

    #region Settings UI

    private bool _updatingUI = false;

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

        PlayerSettingsModel settings = _mediaSettings;
        PlayerSettingsModel presetSettings = _presetSettings ?? settings;

        {
            bool tabModified = false;

            {
                bool modified = settings.Video.Decoder != presetSettings.Video.Decoder;
                tabModified = tabModified || modified;
                UpdateVideoDecoder(settings);
                ViewModel.VideoDecoderLabel = GetLabel(StringResource.Decoder, modified);
            }

            {
                bool modified = settings.Video.Renderer != presetSettings.Video.Renderer;
                tabModified = tabModified || modified;
                UpdateVideoRenderer(settings);
                ViewModel.VideoRendererLabel = GetLabel(StringResource.Renderer, modified);
            }

            {
                bool modified = settings.Video.PreferHardwareDecoding != presetSettings.Video.PreferHardwareDecoding;
                tabModified = tabModified || modified;
                UpdateVideoPreferHardwareDecoding(settings);
                ViewModel.VideoPreferHardwareDecodingLabel = GetLabel(StringResource.PreferHardwareDecoding, modified);
            }

            {
                bool modified = settings.Video.PreferHdr != presetSettings.Video.PreferHdr;
                tabModified = tabModified || modified;
                UpdateVideoPreferHdr(settings);
                ViewModel.VideoPreferHdrLabel = GetLabel(StringResource.PreferHdr, modified);
            }

            {
                bool modified = settings.Video.ResizeMode != presetSettings.Video.ResizeMode;
                tabModified = tabModified || modified;
                UpdateVideoStretchMode(settings);
                ViewModel.VideoStretchModeLabel = GetLabel(StringResource.Scaling, modified);
            }

            {
                bool modified = settings.Video.ScalingMode != presetSettings.Video.ScalingMode;
                tabModified = tabModified || modified;
                UpdateVideoInterpolation(settings);
                ViewModel.VideoInterpolationLabel = GetLabel(StringResource.ImageInterpolation, modified);
            }

            {
                bool modified = settings.Video.RotationDegrees != presetSettings.Video.RotationDegrees;
                tabModified = tabModified || modified;
                UpdateVideoRotation(settings);
                ViewModel.VideoRotationLabel = GetLabel(StringResource.Rotation, modified);
            }

            {
                bool modified = settings.Video.FlipHorizontal != presetSettings.Video.FlipHorizontal;
                tabModified = tabModified || modified;
                UpdateVideoFlipHorizontal(settings);
                ViewModel.VideoFlipHorizontalLabel = GetLabel(StringResource.FlipHorizontally, modified);
            }

            {
                bool modified = settings.Video.FlipVertical != presetSettings.Video.FlipVertical;
                tabModified = tabModified || modified;
                UpdateVideoFlipVertical(settings);
                ViewModel.VideoFlipVerticalLabel = GetLabel(StringResource.FlipVertically, modified);
            }

            _videoTab.Category.IsModified = tabModified;
            ViewModel.VideoTabTitle = GetLabel(StringResource.Video, tabModified);
        }

        {
            bool tabModified = false;

            {
                bool modified = settings.Audio.Decoder != presetSettings.Audio.Decoder;
                tabModified = tabModified || modified;
                UpdateAudioDecoder(settings);
                ViewModel.AudioDecoderLabel = GetLabel(StringResource.Decoder, modified);
            }

            {
                bool modified = settings.Audio.Renderer != presetSettings.Audio.Renderer;
                tabModified = tabModified || modified;
                UpdateAudioRenderer(settings);
                ViewModel.AudioRendererLabel = GetLabel(StringResource.Renderer, modified);
            }

            _audioTab.Category.IsModified = tabModified;
            ViewModel.AudioTabTitle = GetLabel(StringResource.Audio, tabModified);
        }

        {
            bool tabModified = false;

            {
                bool modified = settings.Subtitle.Decoder != presetSettings.Subtitle.Decoder;
                tabModified = tabModified || modified;
                UpdateSubtitleDecoder(settings);
                ViewModel.SubtitleDecoderLabel = GetLabel(StringResource.Decoder, modified);
            }

            {
                bool modified = settings.Subtitle.Renderer != presetSettings.Subtitle.Renderer;
                tabModified = tabModified || modified;
                UpdateSubtitleRenderer(settings);
                ViewModel.SubtitleRendererLabel = GetLabel(StringResource.Renderer, modified);
            }

            {
                bool modified = settings.Subtitle.Order != presetSettings.Subtitle.Order;
                tabModified = tabModified || modified;
                UpdateSubtitleOrder(settings);
                ViewModel.SubtitleOrderLabel = GetLabel(StringResource.Order, modified);
            }

            {
                bool modified = settings.Subtitle.Family != presetSettings.Subtitle.Family;
                tabModified = tabModified || modified;
                UpdateSubtitleFontFamily(settings);
                ViewModel.SubtitleFontFamilyLabel = GetLabel(StringResource.FontFamily, modified);
            }

            {
                bool modified = settings.Subtitle.Size != presetSettings.Subtitle.Size;
                tabModified = tabModified || modified;
                UpdateSubtitleFontSize(settings);
                ViewModel.SubtitleFontSizeLabel = GetLabel(StringResource.FontSize, modified);
            }

            {
                bool modified = settings.Subtitle.Style != presetSettings.Subtitle.Style;
                tabModified = tabModified || modified;
                UpdateSubtitleFontStyle(settings);
                ViewModel.SubtitleFontStyleLabel = GetLabel(StringResource.FontStyle, modified);
            }

            {
                bool modified = settings.Subtitle.Weight != presetSettings.Subtitle.Weight;
                tabModified = tabModified || modified;
                UpdateSubtitleFontWeight(settings);
                ViewModel.SubtitleFontWeightLabel = GetLabel(StringResource.FontWeight, modified);
            }

            {
                bool modified = !settings.Subtitle.FontColor.Equals(presetSettings.Subtitle.FontColor);
                tabModified = tabModified || modified;
                UpdateSubtitleFontColor(settings);
                ViewModel.SubtitleFontColorLabel = GetLabel(StringResource.FontColor, modified);
            }

            {
                bool modified = settings.Subtitle.OutlineWidth != presetSettings.Subtitle.OutlineWidth;
                tabModified = tabModified || modified;
                UpdateSubtitleOutline(settings);
                ViewModel.SubtitleOutlineLabel = GetLabel(StringResource.Outline, modified);
            }

            {
                bool modified = settings.Subtitle.PanX != presetSettings.Subtitle.PanX;
                tabModified = tabModified || modified;
                UpdateSubtitlePanX(settings);
                ViewModel.SubtitlePanXLabel = GetLabel(StringResource.PanX, modified);
            }

            {
                bool modified = settings.Subtitle.PanY != presetSettings.Subtitle.PanY;
                tabModified = tabModified || modified;
                UpdateSubtitlePanY(settings);
                ViewModel.SubtitlePanYLabel = GetLabel(StringResource.PanY, modified);
            }

            _subtitleTab.Category.IsModified = tabModified;
            ViewModel.SubtitleTabTitle = GetLabel(StringResource.Subtitle, tabModified);
        }

        SettingsCategory activeCategory = _activeTab.Category;
        PresetSettingsModel activeSettings = activeCategory.Get(_mediaSettings);

        PresetDropDownButton.Flyout = CreatePresetContextMenu();
        PresetDropDownButton.Content = GetLabel(
            activeSettings.PresetKey == PresetSettingsModel.PRESET_KEY_CUSTOM ?
                StringResourceProvider.Instance.Custom : activeSettings.PresetName,
            activeCategory.IsModified);
    }

    private void UpdateVideoDecoder(PlayerSettingsModel settings)
    {
        List<PlaybackComponentOption> options = [.. PlaybackComponents.GetVideoDecoderOptions()];
        PlaybackComponentOption choice = PlayerMenuCreator.EnsureChoice(options, settings.Video.Decoder);
        VideoDecoderDropDownButton.Flyout = CreateChoiceMenu(options, choice.Key, key =>
        {
            if (settings.Video.Decoder != key)
            {
                Apply(target => target.Video.Decoder = key);
            }
        });
        VideoDecoderDropDownButton.Content = choice.Label;
    }

    private void UpdateVideoRenderer(PlayerSettingsModel settings)
    {
        List<PlaybackComponentOption> options = [.. PlaybackComponents.GetVideoRendererOptions()];
        PlaybackComponentOption choice = PlayerMenuCreator.EnsureChoice(options, settings.Video.Renderer);
        VideoRendererDropDownButton.Flyout = CreateChoiceMenu(options, choice.Key, key =>
        {
            if (settings.Video.Renderer != key)
            {
                Apply(target => target.Video.Renderer = key);
            }
        });
        VideoRendererDropDownButton.Content = choice.Label;
    }

    private void VideoPreferHardwareDecodingToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingUI)
        {
            return;
        }

        bool value = ((ToggleSwitch)sender).IsOn;
        if (_mediaSettings.Video.PreferHardwareDecoding != value)
        {
            Apply(settings => settings.Video.PreferHardwareDecoding = value);
        }
    }

    private void UpdateVideoPreferHardwareDecoding(PlayerSettingsModel settings)
    {
        VideoPreferHardwareDecodingToggleSwitch.IsOn = settings.Video.PreferHardwareDecoding;
    }

    private void VideoPreferHdrToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingUI)
        {
            return;
        }

        bool value = ((ToggleSwitch)sender).IsOn;
        if (_mediaSettings.Video.PreferHdr != value)
        {
            Apply(settings => settings.Video.PreferHdr = value);
        }
    }

    private void UpdateVideoPreferHdr(PlayerSettingsModel settings)
    {
        VideoPreferHdrToggleSwitch.IsOn = settings.Video.PreferHdr;
    }

    private void UpdateVideoStretchMode(PlayerSettingsModel settings)
    {
        List<PlaybackComponentOption> options = PlayerMenuCreator.GetStretchModeOptions();
        PlaybackComponentOption choice = PlayerMenuCreator.EnsureChoice(options, settings.Video.ResizeMode.ToString());
        VideoStretchModeDropDownButton.Flyout = CreateChoiceMenu(options, choice.Key, key =>
        {
            VideoResizeMode mode = Enum.Parse<VideoResizeMode>(key);
            if (settings.Video.ResizeMode != mode)
            {
                Apply(target => target.Video.ResizeMode = mode);
            }
        });
        VideoStretchModeDropDownButton.Content = choice.Label;
    }

    private void UpdateVideoInterpolation(PlayerSettingsModel settings)
    {
        List<PlaybackComponentOption> options = PlayerMenuCreator.GetScalingModeOptions();
        PlaybackComponentOption choice = PlayerMenuCreator.EnsureChoice(options, settings.Video.ScalingMode.ToString());
        VideoInterpolationDropDownButton.Flyout = CreateChoiceMenu(options, choice.Key, key =>
        {
            VideoScalingMode mode = Enum.Parse<VideoScalingMode>(key);
            if (settings.Video.ScalingMode != mode)
            {
                Apply(target => target.Video.ScalingMode = mode);
            }
        });
        VideoInterpolationDropDownButton.Content = choice.Label;
    }

    private void UpdateVideoRotation(PlayerSettingsModel settings)
    {
        List<PlaybackComponentOption> options = PlayerMenuCreator.GetRotationOptions();
        PlaybackComponentOption choice = PlayerMenuCreator.EnsureChoice(options, settings.Video.RotationDegrees.ToString(CultureInfo.InvariantCulture));
        VideoRotationDropDownButton.Flyout = CreateChoiceMenu(options, choice.Key, key =>
        {
            int degrees = int.Parse(key, CultureInfo.InvariantCulture);
            if (settings.Video.RotationDegrees != degrees)
            {
                Apply(target => target.Video.RotationDegrees = degrees);
            }
        });
        VideoRotationDropDownButton.Content = choice.Label;
    }

    private void VideoFlipHorizontalToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingUI)
        {
            return;
        }

        bool value = ((ToggleSwitch)sender).IsOn;
        if (_mediaSettings.Video.FlipHorizontal != value)
        {
            Apply(settings => settings.Video.FlipHorizontal = value);
        }
    }

    private void UpdateVideoFlipHorizontal(PlayerSettingsModel settings)
    {
        VideoFlipHorizontalToggleSwitch.IsOn = settings.Video.FlipHorizontal;
    }

    private void VideoFlipVerticalToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingUI)
        {
            return;
        }

        bool value = ((ToggleSwitch)sender).IsOn;
        if (_mediaSettings.Video.FlipVertical != value)
        {
            Apply(settings => settings.Video.FlipVertical = value);
        }
    }

    private void UpdateVideoFlipVertical(PlayerSettingsModel settings)
    {
        VideoFlipVerticalToggleSwitch.IsOn = settings.Video.FlipVertical;
    }

    private void UpdateAudioDecoder(PlayerSettingsModel settings)
    {
        List<PlaybackComponentOption> options = [.. PlaybackComponents.GetAudioDecoderOptions()];
        PlaybackComponentOption choice = PlayerMenuCreator.EnsureChoice(options, settings.Audio.Decoder);
        AudioDecoderDropDownButton.Flyout = CreateChoiceMenu(options, choice.Key, key =>
        {
            if (settings.Audio.Decoder != key)
            {
                Apply(target => target.Audio.Decoder = key);
            }
        });
        AudioDecoderDropDownButton.Content = choice.Label;
    }

    private void UpdateAudioRenderer(PlayerSettingsModel settings)
    {
        List<PlaybackComponentOption> options = [.. PlaybackComponents.GetAudioRendererOptions()];
        PlaybackComponentOption choice = PlayerMenuCreator.EnsureChoice(options, settings.Audio.Renderer);
        AudioRendererDropDownButton.Flyout = CreateChoiceMenu(options, choice.Key, key =>
        {
            if (settings.Audio.Renderer != key)
            {
                Apply(target => target.Audio.Renderer = key);
            }
        });
        AudioRendererDropDownButton.Content = choice.Label;
    }

    private void UpdateSubtitleDecoder(PlayerSettingsModel settings)
    {
        List<PlaybackComponentOption> options = [.. PlaybackComponents.GetSubtitleDecoderOptions()];
        PlaybackComponentOption choice = PlayerMenuCreator.EnsureChoice(options, settings.Subtitle.Decoder);
        SubtitleDecoderDropDownButton.Flyout = CreateChoiceMenu(options, choice.Key, key =>
        {
            if (settings.Subtitle.Decoder != key)
            {
                Apply(target => target.Subtitle.Decoder = key);
            }
        });
        SubtitleDecoderDropDownButton.Content = choice.Label;
    }

    private void UpdateSubtitleRenderer(PlayerSettingsModel settings)
    {
        List<PlaybackComponentOption> options = [.. PlaybackComponents.GetSubtitleRendererOptions()];
        PlaybackComponentOption choice = PlayerMenuCreator.EnsureChoice(options, settings.Subtitle.Renderer);
        SubtitleRendererDropDownButton.Flyout = CreateChoiceMenu(options, choice.Key, key =>
        {
            if (settings.Subtitle.Renderer != key)
            {
                Apply(target => target.Subtitle.Renderer = key);
            }
        });
        SubtitleRendererDropDownButton.Content = choice.Label;
    }

    private void SubtitleOrderNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingUI)
        {
            return;
        }

        if (double.IsNaN(sender.Value))
        {
            sender.Value = 0;
            if (_mediaSettings.Subtitle.Order != 0)
            {
                Apply(settings => settings.Subtitle.Order = 0);
            }

            return;
        }

        int value = (int)Math.Round(sender.Value);
        if (_mediaSettings.Subtitle.Order != value)
        {
            Apply(settings => settings.Subtitle.Order = value);
        }
    }

    private void UpdateSubtitleOrder(PlayerSettingsModel settings)
    {
        double value = settings.Subtitle.Order;
        double shown = SubtitleOrderNumberBox.Value;
        if (double.IsNaN(shown) || Math.Abs(shown - value) > 0.5)
        {
            SubtitleOrderNumberBox.Value = value;
        }
    }

    private void SubtitlePanXNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingUI)
        {
            return;
        }

        if (double.IsNaN(sender.Value))
        {
            sender.Value = 0;
            if (_mediaSettings.Subtitle.PanX != 0)
            {
                Apply(settings => settings.Subtitle.PanX = 0);
            }

            return;
        }

        int value = (int)Math.Round(sender.Value);
        if (_mediaSettings.Subtitle.PanX != value)
        {
            Apply(settings => settings.Subtitle.PanX = value);
        }
    }

    private void UpdateSubtitlePanX(PlayerSettingsModel settings)
    {
        double value = settings.Subtitle.PanX;
        double shown = SubtitlePanXNumberBox.Value;
        if (double.IsNaN(shown) || Math.Abs(shown - value) > 0.5)
        {
            SubtitlePanXNumberBox.Value = value;
        }
    }

    private void SubtitlePanYNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingUI)
        {
            return;
        }

        if (double.IsNaN(sender.Value))
        {
            sender.Value = 0;
            if (_mediaSettings.Subtitle.PanY != 0)
            {
                Apply(settings => settings.Subtitle.PanY = 0);
            }

            return;
        }

        int value = (int)Math.Round(sender.Value);
        if (_mediaSettings.Subtitle.PanY != value)
        {
            Apply(settings => settings.Subtitle.PanY = value);
        }
    }

    private void UpdateSubtitlePanY(PlayerSettingsModel settings)
    {
        double value = settings.Subtitle.PanY;
        double shown = SubtitlePanYNumberBox.Value;
        if (double.IsNaN(shown) || Math.Abs(shown - value) > 0.5)
        {
            SubtitlePanYNumberBox.Value = value;
        }
    }

    private void SubtitleFontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUI)
        {
            return;
        }

        if (sender is not ComboBox comboBox || comboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string family)
        {
            return;
        }

        if (_mediaSettings.Subtitle.Family != family)
        {
            Apply(settings => settings.Subtitle.Family = family);
        }
    }

    private void UpdateSubtitleFontFamily(PlayerSettingsModel settings)
    {
        int IndexOfSubtitleFontFamily(string family)
        {
            for (int i = 0; i < SubtitleFontFamilyComboBox.Items.Count; i++)
            {
                if (SubtitleFontFamilyComboBox.Items[i] is ComboBoxItem item && item.Tag is string candidate && string.Equals(candidate, family, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        if (SubtitleFontFamilyComboBox.Items.Count == 0)
        {
            SubtitleFontFamilyComboBox.Items.Add(new ComboBoxItem { Content = StringResource.Default, Tag = string.Empty });
            foreach (string family in SubtitleFonts.GetFamilies())
            {
                SubtitleFontFamilyComboBox.Items.Add(new ComboBoxItem { Content = family, Tag = family });
            }
        }

        int index = IndexOfSubtitleFontFamily(settings.Subtitle.Family);
        if (index < 0)
        {
            index = 0;
        }

        if (SubtitleFontFamilyComboBox.SelectedIndex != index)
        {
            SubtitleFontFamilyComboBox.SelectedIndex = index;
        }
    }

    private void SubtitleFontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUI)
        {
            return;
        }

        if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item && item.Tag is double size)
        {
            ApplySubtitleFontSize(size);
        }
    }

    private void SubtitleFontSizeComboBox_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        if (_updatingUI)
        {
            return;
        }

        ApplySubtitleFontSize(ParseSubtitleFontSize(sender.Text));
    }

    private void ApplySubtitleFontSize(double size)
    {
        double value = size <= 0.0 ? 0.0 : Math.Clamp(size, MinimumSubtitleFontSize, MaximumSubtitleFontSize);
        if (_mediaSettings.Subtitle.Size != value)
        {
            Apply(settings => settings.Subtitle.Size = value);
            return;
        }
    }

    private void UpdateSubtitleFontSize(PlayerSettingsModel settings)
    {
        if (SubtitleFontSizeComboBox.Items.Count == 0)
        {
            SubtitleFontSizeComboBox.Items.Add(new ComboBoxItem { Content = StringResource.Default, Tag = 0.0 });
            foreach (double size in SubtitleFontSizePresets)
            {
                SubtitleFontSizeComboBox.Items.Add(new ComboBoxItem { Content = FormatSubtitleFontSize(size), Tag = size });
            }
        }

        double value = settings.Subtitle.Size;
        int index = -1;
        int insertAt = SubtitleFontSizeComboBox.Items.Count;
        for (int i = 0; i < SubtitleFontSizeComboBox.Items.Count; i++)
        {
            if (SubtitleFontSizeComboBox.Items[i] is not ComboBoxItem item || item.Tag is not double candidate)
            {
                continue;
            }

            if (candidate == value)
            {
                index = i;
                break;
            }

            if (candidate > value)
            {
                insertAt = i;
                break;
            }
        }

        if (index < 0)
        {
            SubtitleFontSizeComboBox.Items.Insert(insertAt, new ComboBoxItem { Content = FormatSubtitleFontSize(value), Tag = value });
            index = insertAt;
        }

        if (SubtitleFontSizeComboBox.SelectedIndex != index)
        {
            SubtitleFontSizeComboBox.SelectedIndex = index;
        }
    }

    private void SubtitleFontWeightComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUI)
        {
            return;
        }

        if (sender is not ComboBox comboBox || comboBox.SelectedItem is not ComboBoxItem item || item.Tag is not int weight)
        {
            return;
        }

        if (_mediaSettings.Subtitle.Weight != weight)
        {
            Apply(settings => settings.Subtitle.Weight = weight);
        }
    }

    private string? _subtitleFontWeightFamily;

    private void UpdateSubtitleFontWeight(PlayerSettingsModel settings)
    {
        if (!string.Equals(_subtitleFontWeightFamily, settings.Subtitle.Family, StringComparison.Ordinal))
        {
            _subtitleFontWeightFamily = settings.Subtitle.Family;
            SubtitleFontWeightComboBox.Items.Clear();
            foreach (int weight in SubtitleFonts.GetWeights(settings.Subtitle.Family))
            {
                SubtitleFontWeightComboBox.Items.Add(new ComboBoxItem { Content = FormatSubtitleFontWeight(weight), Tag = weight });
            }
        }

        int value = settings.Subtitle.Weight > 0 ? settings.Subtitle.Weight : DefaultSubtitleFontWeight;
        int index = -1;
        int insertAt = SubtitleFontWeightComboBox.Items.Count;
        for (int i = 0; i < SubtitleFontWeightComboBox.Items.Count; i++)
        {
            if (SubtitleFontWeightComboBox.Items[i] is not ComboBoxItem item || item.Tag is not int candidate)
            {
                continue;
            }

            if (candidate == value)
            {
                index = i;
                break;
            }

            if (candidate > value)
            {
                insertAt = i;
                break;
            }
        }

        if (index < 0)
        {
            SubtitleFontWeightComboBox.Items.Insert(insertAt, new ComboBoxItem { Content = FormatSubtitleFontWeight(value), Tag = value });
            index = insertAt;
        }

        if (SubtitleFontWeightComboBox.SelectedIndex != index)
        {
            SubtitleFontWeightComboBox.SelectedIndex = index;
        }
    }

    private void SubtitleFontStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUI)
        {
            return;
        }

        if (sender is not ComboBox comboBox || comboBox.SelectedItem is not ComboBoxItem item || item.Tag is not FontStyle style)
        {
            return;
        }

        string value = style.ToString();
        if (_mediaSettings.Subtitle.Style != value)
        {
            Apply(settings => settings.Subtitle.Style = value);
        }
    }

    private void UpdateSubtitleFontStyle(PlayerSettingsModel settings)
    {
        if (SubtitleFontStyleComboBox.Items.Count == 0)
        {
            foreach (FontStyle style in Enum.GetValues<FontStyle>())
            {
                SubtitleFontStyleComboBox.Items.Add(new ComboBoxItem { Content = FormatSubtitleFontStyle(style), Tag = style });
            }
        }

        var value = settings.Subtitle.ToFontStyle();
        int index = 0;
        for (int i = 0; i < SubtitleFontStyleComboBox.Items.Count; i++)
        {
            if (SubtitleFontStyleComboBox.Items[i] is ComboBoxItem item && item.Tag is FontStyle candidate && candidate == value)
            {
                index = i;
                break;
            }
        }

        if (SubtitleFontStyleComboBox.SelectedIndex != index)
        {
            SubtitleFontStyleComboBox.SelectedIndex = index;
        }
    }

    private void SubtitleFontColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_updatingUI)
        {
            return;
        }

        Color color = args.NewColor;
        if (!_mediaSettings.Subtitle.FontColor.Equals(color))
        {
            Apply(settings => settings.Subtitle.FontColor = color);
        }
    }

    private void UpdateSubtitleFontColor(PlayerSettingsModel settings)
    {
        Color color = settings.Subtitle.FontColor;
        if (!SubtitleFontColorPicker.Color.Equals(color))
        {
            SubtitleFontColorPicker.Color = color;
        }

        SubtitleFontColorPreview.Background = new SolidColorBrush(color);
    }

    private void SubtitleOutlineSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingUI)
        {
            return;
        }

        double value = e.NewValue;
        if (_mediaSettings.Subtitle.OutlineWidth != value)
        {
            Apply(settings => settings.Subtitle.OutlineWidth = value);
        }
    }

    private void UpdateSubtitleOutline(PlayerSettingsModel settings)
    {
        double value = settings.Subtitle.OutlineWidth;
        if (Math.Abs(SubtitleOutlineSlider.Value - value) > 0.001)
        {
            SubtitleOutlineSlider.Value = value;
        }
    }

    #endregion

    #region Settings

    private void LoadSettings()
    {
        if (_media is not { } media)
        {
            return;
        }

        foreach (SettingsTab tab in _tabs)
        {
            SettingsCategory category = tab.Category;
            PresetSettingsModel current = category.Get(_mediaSettings);
            string? record = KindOf(tab) is { } kind ? PlayerSettingsStore.GetRecord(media, kind, TrackOf(kind)) : null;
            category.Set(_mediaSettings, current.ResolveRecord(record));
        }

        _presetSettings = LoadPresetSettings(_mediaSettings);
    }

    private PlayerSettingsModel LoadPresetSettings(PlayerSettingsModel settings)
    {
        PlayerSettingsModel presets = new();
        foreach (SettingsTab tab in _tabs)
        {
            SettingsCategory category = tab.Category;
            PresetSettingsModel current = category.Get(settings);
            category.Set(presets, current.FindPreset(current.PresetKey) ?? current);
        }

        return presets;
    }

    private void SaveSettings()
    {
        if (_updatingUI || _media is not { } media)
        {
            return;
        }

        foreach (SettingsTab tab in _tabs)
        {
            if (KindOf(tab) is not { } kind)
            {
                continue;
            }

            SaveCategory(media, kind, TrackOf(kind), tab.Category.Get(_mediaSettings));
        }

        _presetSettings = LoadPresetSettings(_mediaSettings);
    }

    private static void SaveCategory(MediaModel media, MediaStreamKind kind, TrackIdentityModel? track, PresetSettingsModel settings)
    {
        string record = settings.ToRecordJson();
        if (track is not null)
        {
            PlayerSettingsStore.SetTrackRecord(media, kind, track, record);
            return;
        }

        if (PlayerSettingsStore.GetGlobalRecord(media, kind) == record)
        {
            return;
        }

        PlayerSettingsStore.SetGlobalRecord(media, kind, record);
        PlayerSettingsStore.ClearTrackRecords(media, kind);
    }

    private void NotifySettingsChanged()
    {
        ViewModel.SettingsChangedLiveData.Emit(true);
    }

    #endregion

    #region Session

    private void AttachSession(PlayerSession? session)
    {
        if (_session != session)
        {
            DetachSessionEvents();
            _session = session;
        }

        if (session is null)
        {
            return;
        }

        session.TrackSelectionChanged -= Session_TrackSelectionChanged;
        session.TrackSelectionChanged += Session_TrackSelectionChanged;
    }

    private void DetachSessionEvents()
    {
        if (_session is null)
        {
            return;
        }

        _session.TrackSelectionChanged -= Session_TrackSelectionChanged;
    }

    private void Session_TrackSelectionChanged()
    {
        CoroutineUtils.RunInMainThread(RefreshTracks);
    }

    private void RefreshTracks()
    {
        if (_media is null || !IsLoaded)
        {
            return;
        }

        RefreshScopeComboBox();
        LoadSettings();
        UpdateUI();
    }

    #endregion

    #region Misc

    private PresetSettingsModel ActiveSettings()
    {
        return _activeTab.Category.Get(_mediaSettings);
    }

    private static PresetSettingsModel WithPresetKey(PresetSettingsModel settings, string presetKey)
    {
        settings.PresetKey = presetKey;
        return settings;
    }

    private static PresetSettingsModel KeepPreset(PresetSettingsModel current, PresetSettingsModel defaults)
    {
        defaults.PresetKey = current.PresetKey;
        defaults.PresetName = current.PresetName;
        defaults.AllowVariation = current.AllowVariation;
        return defaults;
    }

    private void DispatchDataChangeEvent()
    {
        if (_updatingUI)
        {
            return;
        }

        DataChanged?.Invoke();
    }

    private sealed class SettingsCategory
    {
        public required Func<PlayerSettingsModel, PresetSettingsModel> Get { get; init; }
        public required Action<PlayerSettingsModel, PresetSettingsModel> Set { get; init; }
        public bool IsModified { get; set; }
    }

    private sealed class SettingsTab
    {
        public required string Key { get; init; }
        public required SelectorBarItem Item { get; init; }
        public required StackPanel Content { get; init; }
        public required SettingsCategory Category { get; init; }
    }

    #endregion

    #region Utilities

    private static MenuFlyout CreateChoiceMenu(IReadOnlyList<PlaybackComponentOption> options, string? selectedKey, Action<string> select)
    {
        var flyout = new MenuFlyout();
        foreach (BaseMenuFlyoutItemModel item in PlayerMenuCreator.CreateChoiceItems(options, selectedKey, select))
        {
            flyout.Items.Add(item.CreateMenuFlyoutItem());
        }

        return flyout;
    }

    private string FormatSubtitleFontSize(double size)
    {
        return size > 0.0 ? size.ToString("0.##", CultureInfo.CurrentCulture) : StringResource.Default;
    }

    private string FormatSubtitleFontWeight(int weight)
    {
        string number = weight.ToString(CultureInfo.CurrentCulture);
        return weight == DefaultSubtitleFontWeight ? $"{number} ({StringResource.Default})" : number;
    }

    private string FormatSubtitleFontStyle(FontStyle style)
    {
        return style switch
        {
            FontStyle.Oblique => StringResource.FontStyleOblique,
            FontStyle.Italic => StringResource.FontStyleItalic,
            _ => StringResource.FontStyleNormal,
        };
    }

    private static double ParseSubtitleFontSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0.0;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double size) && double.IsFinite(size))
        {
            return size;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out size) && double.IsFinite(size))
        {
            return size;
        }

        return 0.0;
    }

    #endregion
}
