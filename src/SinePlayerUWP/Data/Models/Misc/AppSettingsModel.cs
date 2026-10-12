// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using SinePlayerUWP.Common.HotKey;
using SinePlayerUWP.Common.Utils;
using SinePlayerUWP.Core.Common.AppEnvironment;
using SinePlayerUWP.Core.Common.DebugTools;
using SinePlayerUWP.Core.Common.Lifecycle;
using SinePlayerUWP.Core.Database.JSON;

using Windows.Globalization;
using Windows.System;

namespace SinePlayerUWP.Data.Models.Misc;

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

    public static OpenMediaBehaviorEnum OpenMediaDefaultBehavior
    {
        get
        {
            return _db.Read(model => ConvertOpenMediaDefaultBehaviorFromJson(model.OpenMediaDefaultBehavior));
        }
        set
        {
            _db.Write(model => model.OpenMediaDefaultBehavior = ConvertOpenMediaDefaultBehaviorToJson(value));
            _db.Save();
        }
    }

    public static bool PlaybackDefaultRepeat
    {
        get
        {
            return _db.Read(model => model.PlaybackDefaultRepeat ?? false);
        }
        set
        {
            _db.Write(model => model.PlaybackDefaultRepeat = value);
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

    public static bool RestoreLastReadingPosition
    {
        get
        {
            return _db.Read(model => model.RestoreLastReadingPosition ?? true);
        }
        set
        {
            _db.Write(model => model.RestoreLastReadingPosition = value);
            _db.Save();
        }
    }

    public static bool RestoreLastReadingPositionOnlyAppliesToReadingMedia
    {
        get
        {
            return _db.Read(model => model.RestoreLastReadingPositionOnlyAppliesToReadingMedia ?? false);
        }
        set
        {
            _db.Write(model => model.RestoreLastReadingPositionOnlyAppliesToReadingMedia = value);
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

    //
    // Getters
    //

    public static ExternalModel GetModel()
    {
        return _db.Read(ExternalModel.From);
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

    public static void UpdateModel(ExternalModel model)
    {
        _db.Write(model.To);
        _db.Save();
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

    private static string ConvertOpenMediaDefaultBehaviorToJson(OpenMediaBehaviorEnum behavior)
    {
        return behavior switch
        {
            OpenMediaBehaviorEnum.OpenInCurrentTab => "OpenInCurrentTab",
            OpenMediaBehaviorEnum.OpenInNewTab => "OpenInNewTab",
            OpenMediaBehaviorEnum.OpenInLastActiveReaderTab => "OpenInLastActiveReaderTab",
            _ => "OpenInCurrentTab",
        };
    }

    private static OpenMediaBehaviorEnum ConvertOpenMediaDefaultBehaviorFromJson(string? behavior)
    {
        return behavior switch
        {
            "OpenInCurrentTab" => OpenMediaBehaviorEnum.OpenInCurrentTab,
            "OpenInNewTab" => OpenMediaBehaviorEnum.OpenInNewTab,
            "OpenInLastActiveReaderTab" => OpenMediaBehaviorEnum.OpenInLastActiveReaderTab,
            _ => OpenMediaBehaviorEnum.OpenInCurrentTab,
        };
    }

    private static string ConvertKeepScreenOnBehaviorToJson(KeepScreenOnBehaviorEnum behavior)
    {
        return behavior switch
        {
            KeepScreenOnBehaviorEnum.Never => "Never",
            KeepScreenOnBehaviorEnum.Always => "Always",
            KeepScreenOnBehaviorEnum.WhilePlayingVideos => "DuringPlayback",
            _ => "DuringAutoScrolling",
        };
    }

    private static KeepScreenOnBehaviorEnum ConvertKeepScreenOnBehaviorFromJson(string? behavior)
    {
        return behavior switch
        {
            "Never" => KeepScreenOnBehaviorEnum.Never,
            "Always" => KeepScreenOnBehaviorEnum.Always,
            "DuringPlayback" => KeepScreenOnBehaviorEnum.WhilePlayingVideos,
            _ => KeepScreenOnBehaviorEnum.WhilePlayingVideos,
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
            model.OpenMediaDefaultBehavior ??= model.HomePageTapMediaBehavior;
            return model;
        }
    }

    public class ExternalModel
    {
        public List<string> MediaFolders { get; set; } = [];
        public bool ScanOnLaunch { get; set; }
        public bool RemoveUnreachableMedia { get; set; }
        public bool PromptBeforeRemovingMedia { get; set; }
        public AppearanceSetting Theme { get; set; } = AppearanceSetting.UseSystemSetting;
        public int MediaShuffleRandomSeed { get; set; }

        public static ExternalModel From(JsonModel model)
        {
            ExternalModel externalModel = new()
            {
                ScanOnLaunch = model.ScanOnLaunch ?? true,
                RemoveUnreachableMedia = model.RemoveUnreachableMedia ?? true,
                PromptBeforeRemovingMedia = model.PromptBeforeRemovingMedia ?? true,
                MediaShuffleRandomSeed = model.MediaShuffleRandomSeed ?? 0,
            };

            if (model.MediaFolders is not null)
            {
                foreach (string? folder in model.MediaFolders)
                {
                    if (string.IsNullOrEmpty(folder))
                    {
                        continue;
                    }
                    externalModel.MediaFolders.Add(folder);
                }
            }

            int? theme = model.Theme;
            if (theme is null)
            {
                externalModel.Theme = AppearanceSetting.UseSystemSetting;
            }
            else if (Enum.IsDefined(typeof(AppearanceSetting), theme))
            {
                externalModel.Theme = (AppearanceSetting)theme;
            }
            else
            {
                externalModel.Theme = AppearanceSetting.UseSystemSetting;
            }

            return externalModel;
        }

        public void To(JsonModel model)
        {
            model.MediaFolders = [.. MediaFolders];
            model.ScanOnLaunch = ScanOnLaunch;
            model.RemoveUnreachableMedia = RemoveUnreachableMedia;
            model.PromptBeforeRemovingMedia = PromptBeforeRemovingMedia;
            model.Theme = (int)Theme;
            model.MediaShuffleRandomSeed = MediaShuffleRandomSeed;
        }
    }

    public enum CloseLastTabBehaviorEnum
    {
        CloseWindow,
        OpenHomePage,
    }

    public enum OpenMediaBehaviorEnum
    {
        OpenInCurrentTab,
        OpenInNewTab,
        OpenInLastActiveReaderTab,
    }

    public enum KeepScreenOnBehaviorEnum
    {
        Never,
        Always,
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
        [JsonPropertyName("AutoPlayNext")]
        public bool? AutoPlayNext { get; set; }

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

        [JsonPropertyName("OpenMediaDefaultBehavior")]
        public string? OpenMediaDefaultBehavior { get; set; }

        [JsonPropertyName("Language")]
        public string? Language { get; set; }

        [JsonPropertyName("PlaybackDefaultRepeat")]
        public bool? PlaybackDefaultRepeat { get; set; }

        [JsonPropertyName("PlaybackDefaultShuffle")]
        public bool? PlaybackDefaultShuffle { get; set; }

        [JsonPropertyName("PlayerMuted")]
        public bool? PlayerMuted { get; set; }

        [JsonPropertyName("PlayerVolume")]
        public double? PlayerVolume { get; set; }

        [JsonPropertyName("PromptBeforeRemovingMedia")]
        public bool? PromptBeforeRemovingMedia { get; set; }

        [JsonPropertyName("RatingPercentageEnabled")]
        public bool? RatingPercentageEnabled { get; set; }

        [JsonPropertyName("RemoveUnreachableMedia")]
        public bool? RemoveUnreachableMedia { get; set; }

        [JsonPropertyName("RestoreLastReadingPosition")]
        public bool? RestoreLastReadingPosition { get; set; }

        [JsonPropertyName("RestoreLastReadingPositionOnlyAppliesToReadingMedia")]
        public bool? RestoreLastReadingPositionOnlyAppliesToReadingMedia { get; set; }

        [JsonPropertyName("SendUsageData")]
        public bool? SendUsageData { get; set; }

        [JsonPropertyName("SaveBrowsingHistory")]
        public bool? SaveBrowsingHistory { get; set; }

        [JsonPropertyName("ScanOnLaunch")]
        public bool? ScanOnLaunch { get; set; }

        [JsonPropertyName("Theme")]
        public int? Theme { get; set; }

        //
        // Legacy
        //

        [JsonPropertyName("HomePageTapMediaBehavior")]
        public string? HomePageTapMediaBehavior { get; set; }
    }
}
