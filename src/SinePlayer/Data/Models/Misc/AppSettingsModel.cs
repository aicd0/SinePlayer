// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using SinePlayer.Common.HotKey;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.AppEnvironment;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Database.JSON;
using SinePlayer.Data.Models.Playback;

using Windows.Globalization;
using Windows.System;

namespace SinePlayer.Data.Models.Misc;

internal static class AppSettingsModel
{
    private const string TAG = nameof(AppSettingsModel);

    private static readonly JsonDatabase _db = new();

    //
    // Events
    //

    private static readonly MutableLiveData<bool> _keepScreenOnBehaviorChangeLiveData = new();
    public static ILiveData<bool> KeepScreenOnBehaviorChangedLiveData => _keepScreenOnBehaviorChangeLiveData;

    private static readonly MutableLiveData<bool> _keyboardShortcutsChangedLiveData = new();
    public static ILiveData<bool> KeyboardShortcutsChangedLiveData => _keyboardShortcutsChangedLiveData;

    //
    // Properties
    //

    public static AppBackgroundEnum AppBackground
    {
        get
        {
            return _db.Read(model => ConvertAppBackgroundFromJson(model.Background));
        }
        set
        {
            _db.Write(model => model.Background = ConvertAppBackgroundToJson(value));
            _db.Save();
        }
    }

    public static bool AutoDetectCollections
    {
        get
        {
            return _db.Read(model => model.AutoDetectCollections ?? false);
        }
        set
        {
            _db.Write(model => model.AutoDetectCollections = value);
            _db.Save();
        }
    }

    public static bool AutoSwitch
    {
        get
        {
            return _db.Read(model => model.AutoSwitch ?? true);
        }
        set
        {
            _db.Write(model => model.AutoSwitch = value);
            _db.Save();
        }
    }

    public static bool AutoToggleOverlaysOnCursor
    {
        get
        {
            return _db.Read(model => model.AutoToggleOverlaysOnCursor ?? true);
        }
        set
        {
            _db.Write(model => model.AutoToggleOverlaysOnCursor = value);
            _db.Save();
        }
    }

    public static bool AutoPlayNext
    {
        get
        {
            return _db.Read(model => model.AutoPlayNext ?? true);
        }
        set
        {
            _db.Write(model => model.AutoPlayNext = value);
            _db.Save();
        }
    }

    public static bool AutoPlayOnOpen
    {
        get
        {
            return _db.Read(model => model.AutoPlayOnOpen ?? true);
        }
        set
        {
            _db.Write(model => model.AutoPlayOnOpen = value);
            _db.Save();
        }
    }

    public static bool AutoPlayOnOpenExceptAppLaunch
    {
        get
        {
            return _db.Read(model => model.AutoPlayOnOpenExceptAppLaunch ?? true);
        }
        set
        {
            _db.Write(model => model.AutoPlayOnOpenExceptAppLaunch = value);
            _db.Save();
        }
    }

    public static bool PauseOnLeaveTab
    {
        get
        {
            return _db.Read(model => model.PauseOnLeaveTab ?? true);
        }
        set
        {
            _db.Write(model => model.PauseOnLeaveTab = value);
            _db.Save();
        }
    }

    public static bool PlayerMuted
    {
        get
        {
            return _db.Read(model => model.PlayerMuted ?? false);
        }
        set
        {
            _db.Write(model => model.PlayerMuted = value);
            _db.Save();
        }
    }

    public static double PlayerVolume
    {
        get
        {
            return _db.Read(model => model.PlayerVolume ?? 1.0);
        }
        set
        {
            _db.Write(model => model.PlayerVolume = value);
            _db.Save();
        }
    }

    public static bool PreciseSeeking
    {
        get
        {
            return _db.Read(model => model.PreciseSeeking ?? false);
        }
        set
        {
            _db.Write(model => model.PreciseSeeking = value);
            _db.Save();
        }
    }

    public static CloseLastTabBehaviorEnum CloseLastTabBehavior
    {
        get
        {
            return _db.Read(model => ConvertCloseLastTabBehaviorFromJson(model.CloseLastTabBehavior));
        }
        set
        {
            _db.Write(model => model.CloseLastTabBehavior = ConvertCloseLastTabBehaviorToJson(value));
            _db.Save();
        }
    }

    public static KeepScreenOnBehaviorEnum KeepScreenOnBehavior
    {
        get
        {
            return _db.Read(model => ConvertKeepScreenOnBehaviorFromJson(model.KeepScreenOnBehavior));
        }
        set
        {
            _db.Write(model => model.KeepScreenOnBehavior = ConvertKeepScreenOnBehaviorToJson(value));
            _db.Save();
            _keepScreenOnBehaviorChangeLiveData.Emit(true);
        }
    }

    public static List<KeyboardShortcutModel> KeyboardShortcuts
    {
        get
        {
            return _db.Read(model =>
            {
                List<KeyboardShortcutModel> shortcuts = [];
                if (model.KeyboardShortcuts is null)
                {
                    shortcuts.AddRange(KeyboardShortcutActions.DefaultShortcuts);
                }
                else
                {
                    foreach (KeyboardShortcutJsonModel? jsonModel in model.KeyboardShortcuts)
                    {
                        if (jsonModel is null)
                        {
                            continue;
                        }

                        string action = jsonModel.Action ?? string.Empty;
                        if (string.IsNullOrEmpty(action))
                        {
                            continue;
                        }

                        if (!Enum.TryParse(jsonModel.Key, out VirtualKey key) || key == VirtualKey.None)
                        {
                            continue;
                        }

                        VirtualKeyModifiers modifiers = VirtualKeyModifiers.None;
                        if (!string.IsNullOrEmpty(jsonModel.Modifiers) && !Enum.TryParse(jsonModel.Modifiers, out modifiers))
                        {
                            continue;
                        }

                        shortcuts.Add(new KeyboardShortcutModel
                        {
                            Action = action,
                            Key = key,
                            Modifiers = modifiers,
                        });
                    }
                }

                return shortcuts;
            });
        }
        set
        {
            _db.Write(model =>
            {
                List<KeyboardShortcutJsonModel?> jsonModels = [];
                foreach (KeyboardShortcutModel shortcut in value)
                {
                    if (shortcut.Key == VirtualKey.None)
                    {
                        continue;
                    }

                    jsonModels.Add(new KeyboardShortcutJsonModel
                    {
                        Action = shortcut.Action,
                        Key = shortcut.Key.ToString(),
                        Modifiers = shortcut.Modifiers.ToString(),
                    });
                }

                model.KeyboardShortcuts = jsonModels;
            });
            _db.Save();
            _keyboardShortcutsChangedLiveData.Emit(true);
        }
    }

    public static string Language
    {
        get
        {
            return _db.Read(model => model.Language ?? string.Empty);
        }
        set
        {
            if (!EnvironmentProvider.IsPortable())
            {
                try
                {
                    ApplicationLanguages.PrimaryLanguageOverride = value;
                }
                catch (Exception ex)
                {
                    Logger.F(TAG, ex);
                }
            }

            _db.Write(model => model.Language = value);
            _db.Save();
        }
    }

    public static OpenItemBehaviorEnum OpenItemDefaultBehavior
    {
        get
        {
            return _db.Read(model => ConvertOpenItemDefaultBehaviorFromJson(model.OpenItemDefaultBehavior));
        }
        set
        {
            _db.Write(model => model.OpenItemDefaultBehavior = ConvertOpenItemDefaultBehaviorToJson(value));
            _db.Save();
        }
    }

    public static RepeatMode PlaybackDefaultRepeatMode
    {
        get
        {
            return _db.Read(model => ConvertRepeatModeFromJson(model.PlaybackDefaultRepeatMode));
        }
        set
        {
            _db.Write(model => model.PlaybackDefaultRepeatMode = ConvertRepeatModeToJson(value));
            _db.Save();
        }
    }

    public static bool PlaybackDefaultShuffle
    {
        get
        {
            return _db.Read(model => model.PlaybackDefaultShuffle ?? false);
        }
        set
        {
            _db.Write(model => model.PlaybackDefaultShuffle = value);
            _db.Save();
        }
    }

    public static string DefaultPlayerSettingPresetKey
    {
        get
        {
            return _db.Read(model => model.DefaultPlayerSettingPresetKey ?? string.Empty);
        }
        set
        {
            _db.Write(model => model.DefaultPlayerSettingPresetKey = value);
            _db.Save();
        }
    }

    public static Dictionary<string, PlayerSettingsModel> PlayerSettingPresets
    {
        get
        {
            return _db.Read(model =>
            {
                Dictionary<string, PlayerSettingsModel> presets = [];
                if (model.PlayerSettingPresets is not null)
                {
                    foreach (KeyValuePair<string, PlayerSettingsModel.JsonModel?> kvp in model.PlayerSettingPresets)
                    {
                        string key = kvp.Key;
                        PlayerSettingsModel.JsonModel? settingJsonModel = kvp.Value;
                        if (settingJsonModel is not null)
                        {
                            presets[key] = PlayerSettingsModel.FromJsonModel(key, settingJsonModel);
                        }
                    }
                }

                return presets;
            });
        }
        set
        {
            _db.Write(model =>
            {
                model.PlayerSettingPresets = [];
                foreach (KeyValuePair<string, PlayerSettingsModel> kvp in value)
                {
                    string key = kvp.Key;
                    PlayerSettingsModel setting = kvp.Value;
                    model.PlayerSettingPresets[key] = setting.ToJsonModel();
                }
            });
            _db.Save();
        }
    }

    public static bool RatingPercentageEnabled
    {
        get
        {
            return _db.Read(model => model.RatingPercentageEnabled ?? false);
        }
        set
        {
            _db.Write(model => model.RatingPercentageEnabled = value);
            _db.Save();
        }
    }

    public static bool RestoreLastPlaybackPosition
    {
        get
        {
            return _db.Read(model => model.RestoreLastPlaybackPosition ?? true);
        }
        set
        {
            _db.Write(model => model.RestoreLastPlaybackPosition = value);
            _db.Save();
        }
    }

    public static bool RestoreWindowState
    {
        get
        {
            return _db.Read(model => model.RestoreWindowState ?? false);
        }
        set
        {
            _db.Write(model => model.RestoreWindowState = value);
            _db.Save();
        }
    }

    public static bool SendUsageData
    {
        get
        {
            return _db.Read(model => model.SendUsageData ?? true);
        }
        set
        {
            _db.Write(model => model.SendUsageData = value);
            _db.Save();
        }
    }

    public static bool SaveBrowsingHistory
    {
        get
        {
            return _db.Read(model => model.SaveBrowsingHistory ?? true);
        }
        set
        {
            _db.Write(model => model.SaveBrowsingHistory = value);
            _db.Save();
        }
    }

    public static bool PromptBeforeRemovingMedia
    {
        get
        {
            return _db.Read(model => model.PromptBeforeRemovingMedia ?? true);
        }
        set
        {
            _db.Write(model => model.PromptBeforeRemovingMedia = value);
            _db.Save();
        }
    }

    public static bool RemoveUnreachableMedia
    {
        get
        {
            return _db.Read(model => model.RemoveUnreachableMedia ?? true);
        }
        set
        {
            _db.Write(model => model.RemoveUnreachableMedia = value);
            _db.Save();
        }
    }

    public static bool ScanOnLaunch
    {
        get
        {
            return _db.Read(model => model.ScanOnLaunch ?? true);
        }
        set
        {
            _db.Write(model => model.ScanOnLaunch = value);
            _db.Save();
        }
    }

    public static bool UseFolderNameAsTitle
    {
        get
        {
            return _db.Read(model => model.UseFolderNameAsTitle ?? false);
        }
        set
        {
            _db.Write(model => model.UseFolderNameAsTitle = value);
            _db.Save();
        }
    }

    public static List<string> MediaFolders
    {
        get
        {
            return _db.Read(model =>
            {
                List<string> folders = [];
                if (model.MediaFolders is not null)
                {
                    foreach (string? folder in model.MediaFolders)
                    {
                        if (!string.IsNullOrEmpty(folder))
                        {
                            folders.Add(folder);
                        }
                    }
                }
                return folders;
            });
        }
    }

    public static int MediaShuffleRandomSeed
    {
        get
        {
            return _db.Read(model => model.MediaShuffleRandomSeed ?? 0);
        }
        set
        {
            _db.Write(model => model.MediaShuffleRandomSeed = value);
            _db.Save();
        }
    }

    public static AppearanceSetting Theme
    {
        get
        {
            return _db.Read(model =>
            {
                int? theme = model.Theme;
                if (theme is not null && Enum.IsDefined(typeof(AppearanceSetting), theme))
                {
                    return (AppearanceSetting)theme;
                }
                return AppearanceSetting.UseSystemSetting;
            });
        }
        set
        {
            _db.Write(model => model.Theme = (int)value);
            _db.Save();
        }
    }

    //
    // Setters
    //

    public static void Reset()
    {
        JsonModel newModel = new();
        _db.Read(model =>
        {
            newModel.MediaFolders = model.MediaFolders;
        });

        _db.Write(newModel);

        _keepScreenOnBehaviorChangeLiveData.Emit(true);
        _keyboardShortcutsChangedLiveData.Emit(true);
        Language = Language; // Language config needs to be applied immediately to take effect on next launch
    }

    public static void AddMediaFolder(string folderPath)
    {
        bool updated = _db.Write(m =>
        {
            m.MediaFolders ??= [];
            for (int i = m.MediaFolders.Count - 1; i >= 0; i--)
            {
                string? oldPath = m.MediaFolders[i];
                if (string.IsNullOrEmpty(oldPath))
                {
                    m.MediaFolders.RemoveAt(i);
                    continue;
                }
                if (StringUtils.FolderContain(oldPath, folderPath))
                {
                    return false;
                }
                if (StringUtils.FolderContain(folderPath, oldPath))
                {
                    m.MediaFolders.RemoveAt(i);
                }
            }
            m.MediaFolders.Add(folderPath);
            return true;
        });

        if (updated)
        {
            _db.Save();
        }
    }

    public static void RemoveMediaFolder(string folderPath)
    {
        bool updated = _db.Write(m =>
        {
            m.MediaFolders ??= [];
            return m.MediaFolders.Remove(folderPath);
        });

        if (updated)
        {
            _db.Save();
        }
    }

    //
    // Helpers
    //

    private static string ConvertCloseLastTabBehaviorToJson(CloseLastTabBehaviorEnum behavior)
    {
        return behavior switch
        {
            CloseLastTabBehaviorEnum.CloseWindow => "CloseWindow",
            CloseLastTabBehaviorEnum.OpenHomePage => "OpenHomePage",
            _ => "CloseWindow",
        };
    }

    private static CloseLastTabBehaviorEnum ConvertCloseLastTabBehaviorFromJson(string? behavior)
    {
        return behavior switch
        {
            "CloseWindow" => CloseLastTabBehaviorEnum.CloseWindow,
            "OpenHomePage" => CloseLastTabBehaviorEnum.OpenHomePage,
            _ => CloseLastTabBehaviorEnum.CloseWindow,
        };
    }

    private static string ConvertOpenItemDefaultBehaviorToJson(OpenItemBehaviorEnum behavior)
    {
        return behavior switch
        {
            OpenItemBehaviorEnum.OpenInCurrentTab => "OpenInCurrentTab",
            OpenItemBehaviorEnum.OpenInNewTab => "OpenInNewTab",
            OpenItemBehaviorEnum.OpenInLastActivePlayerTab => "OpenInLastActivePlayerTab",
            _ => "OpenInCurrentTab",
        };
    }

    private static OpenItemBehaviorEnum ConvertOpenItemDefaultBehaviorFromJson(string? behavior)
    {
        return behavior switch
        {
            "OpenInCurrentTab" => OpenItemBehaviorEnum.OpenInCurrentTab,
            "OpenInNewTab" => OpenItemBehaviorEnum.OpenInNewTab,
            "OpenInLastActivePlayerTab" => OpenItemBehaviorEnum.OpenInLastActivePlayerTab,
            _ => OpenItemBehaviorEnum.OpenInCurrentTab,
        };
    }

    private static string ConvertKeepScreenOnBehaviorToJson(KeepScreenOnBehaviorEnum behavior)
    {
        return behavior switch
        {
            KeepScreenOnBehaviorEnum.Never => "Never",
            KeepScreenOnBehaviorEnum.Always => "Always",
            KeepScreenOnBehaviorEnum.WhilePlaying => "WhilePlaying",
            KeepScreenOnBehaviorEnum.WhilePlayingVideos => "WhilePlayingVideos",
            _ => "WhilePlayingVideos",
        };
    }

    private static KeepScreenOnBehaviorEnum ConvertKeepScreenOnBehaviorFromJson(string? behavior)
    {
        return behavior switch
        {
            "Never" => KeepScreenOnBehaviorEnum.Never,
            "Always" => KeepScreenOnBehaviorEnum.Always,
            "WhilePlaying" => KeepScreenOnBehaviorEnum.WhilePlaying,
            "WhilePlayingVideos" => KeepScreenOnBehaviorEnum.WhilePlayingVideos,
            _ => KeepScreenOnBehaviorEnum.WhilePlayingVideos,
        };
    }

    private static string ConvertRepeatModeToJson(RepeatMode repeatMode)
    {
        return repeatMode switch
        {
            RepeatMode.All => "All",
            RepeatMode.One => "One",
            _ => "Off",
        };
    }

    private static RepeatMode ConvertRepeatModeFromJson(string? repeatMode)
    {
        return repeatMode switch
        {
            "All" => RepeatMode.All,
            "One" => RepeatMode.One,
            _ => RepeatMode.Off,
        };
    }

    private static string ConvertAppBackgroundToJson(AppBackgroundEnum background)
    {
        return background switch
        {
            AppBackgroundEnum.None => "None",
            AppBackgroundEnum.Acrylic => "Acrylic",
            AppBackgroundEnum.Mica => "Mica",
            AppBackgroundEnum.MicaAlt => "MicaAlt",
            _ => "None",
        };
    }

    private static AppBackgroundEnum ConvertAppBackgroundFromJson(string? background)
    {
        return background switch
        {
            "None" => AppBackgroundEnum.None,
            "Acrylic" => AppBackgroundEnum.Acrylic,
            "Mica" => AppBackgroundEnum.Mica,
            "MicaAlt" => AppBackgroundEnum.MicaAlt,
            _ => AppBackgroundEnum.None,
        };
    }

    //
    // Types
    //

    private class JsonDatabase : JsonDatabase<JsonModel>
    {
        public JsonDatabase() : base(new SimpleConfigDatabaseLayer("Settings.json")) { }

        protected override JsonModel InitializeModel(JsonModel? model)
        {
            model ??= new();
            return model;
        }
    }

    public enum CloseLastTabBehaviorEnum
    {
        CloseWindow,
        OpenHomePage,
    }

    public enum OpenItemBehaviorEnum
    {
        OpenInCurrentTab,
        OpenInNewTab,
        OpenInLastActivePlayerTab,
    }

    public enum KeepScreenOnBehaviorEnum
    {
        Never,
        Always,
        WhilePlaying,
        WhilePlayingVideos,
    }

    public enum AppearanceSetting
    {
        Light,
        Dark,
        UseSystemSetting,
        None
    }

    public enum AppBackgroundEnum
    {
        None,
        Acrylic,
        Mica,
        MicaAlt,
    }

    public class KeyboardShortcutJsonModel
    {
        [JsonPropertyName("Action")]
        public string? Action { get; set; }

        [JsonPropertyName("Key")]
        public string? Key { get; set; }

        [JsonPropertyName("Modifiers")]
        public string? Modifiers { get; set; }
    }

    public class JsonModel
    {
        [JsonPropertyName("AutoDetectCollections")]
        public bool? AutoDetectCollections { get; set; }

        [JsonPropertyName("AutoPlayNext")]
        public bool? AutoPlayNext { get; set; }

        [JsonPropertyName("AutoPlayOnOpen")]
        public bool? AutoPlayOnOpen { get; set; }

        [JsonPropertyName("AutoPlayOnOpenExceptAppLaunch")]
        public bool? AutoPlayOnOpenExceptAppLaunch { get; set; }

        [JsonPropertyName("PauseOnLeaveTab")]
        public bool? PauseOnLeaveTab { get; set; }

        [JsonPropertyName("AutoSwitch")]
        public bool? AutoSwitch { get; set; }

        [JsonPropertyName("AutoToggleOverlaysOnCursor")]
        public bool? AutoToggleOverlaysOnCursor { get; set; }

        [JsonPropertyName("Background")]
        public string? Background { get; set; }

        [JsonPropertyName("CloseLastTabBehavior")]
        public string? CloseLastTabBehavior { get; set; }

        [JsonPropertyName("MediaFolders")]
        public List<string?>? MediaFolders { get; set; }

        [JsonPropertyName("MediaShuffleRandomSeed")]
        public int? MediaShuffleRandomSeed { get; set; }

        [JsonPropertyName("KeepScreenOnBehavior")]
        public string? KeepScreenOnBehavior { get; set; }

        [JsonPropertyName("KeyboardShortcuts")]
        public List<KeyboardShortcutJsonModel?>? KeyboardShortcuts { get; set; }

        [JsonPropertyName("OpenItemDefaultBehavior")]
        public string? OpenItemDefaultBehavior { get; set; }

        [JsonPropertyName("Language")]
        public string? Language { get; set; }

        [JsonPropertyName("PlaybackDefaultRepeatMode")]
        public string? PlaybackDefaultRepeatMode { get; set; }

        [JsonPropertyName("PlaybackDefaultShuffle")]
        public bool? PlaybackDefaultShuffle { get; set; }

        [JsonPropertyName("DefaultPlayerSettingPresetKey")]
        public string? DefaultPlayerSettingPresetKey { get; set; }

        [JsonPropertyName("PlayerSettingPresets")]
        public Dictionary<string, PlayerSettingsModel.JsonModel?>? PlayerSettingPresets { get; set; }

        [JsonPropertyName("PlayerMuted")]
        public bool? PlayerMuted { get; set; }

        [JsonPropertyName("PlayerVolume")]
        public double? PlayerVolume { get; set; }

        [JsonPropertyName("PreciseSeeking")]
        public bool? PreciseSeeking { get; set; }

        [JsonPropertyName("PromptBeforeRemovingMedia")]
        public bool? PromptBeforeRemovingMedia { get; set; }

        [JsonPropertyName("RatingPercentageEnabled")]
        public bool? RatingPercentageEnabled { get; set; }

        [JsonPropertyName("RemoveUnreachableMedia")]
        public bool? RemoveUnreachableMedia { get; set; }

        [JsonPropertyName("RestoreLastPlaybackPosition")]
        public bool? RestoreLastPlaybackPosition { get; set; }

        [JsonPropertyName("RestoreWindowState")]
        public bool? RestoreWindowState { get; set; }

        [JsonPropertyName("SendUsageData")]
        public bool? SendUsageData { get; set; }

        [JsonPropertyName("SaveBrowsingHistory")]
        public bool? SaveBrowsingHistory { get; set; }

        [JsonPropertyName("ScanOnLaunch")]
        public bool? ScanOnLaunch { get; set; }

        [JsonPropertyName("Theme")]
        public int? Theme { get; set; }

        [JsonPropertyName("UseFolderNameAsTitle")]
        public bool? UseFolderNameAsTitle { get; set; }
    }
}
