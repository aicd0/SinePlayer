// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using SinePlayer.Common.Localization;
using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Views.Dialogs.EditPlayerSettingPreset;

internal partial class EditPlayerSettingPresetDialogViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    private bool _setAsDefault = false;
    public bool SetAsDefault
    {
        get => _setAsDefault;
        set
        {
            _setAsDefault = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SetAsDefault)));
        }
    }

    private bool _allowVariation = true;
    public bool AllowVariation
    {
        get => _allowVariation;
        set
        {
            _allowVariation = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AllowVariation)));
        }
    }

    private bool _saveEnabled = false;
    public bool SaveEnabled
    {
        get => _saveEnabled;
        set
        {
            _saveEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SaveEnabled)));
        }
    }

    private bool _saveAsNewEnabled = false;
    public bool SaveAsNewEnabled
    {
        get => _saveAsNewEnabled;
        set
        {
            _saveAsNewEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SaveAsNewEnabled)));
        }
    }

    private bool _deleteEnabled = false;
    public bool DeleteEnabled
    {
        get => _deleteEnabled;
        set
        {
            _deleteEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeleteEnabled)));
        }
    }

    private PresetSettingsModel _presetModel = VideoSettingsModel.FromDefault();

    public void Initialize(PresetSettingsModel settings)
    {
        _presetModel = settings;

        Title = StringResourceProvider.Instance.EditPreset;
        Name = _presetModel.PresetName;
        SetAsDefault = _presetModel.PresetKey == LoadDefaultPresetKey(_presetModel);
        AllowVariation = _presetModel.AllowVariation;
        UpdateUI();
    }

    public void Delete()
    {
        if (_presetModel.PresetKey == PresetSettingsModel.PRESET_KEY_CUSTOM)
        {
            return;
        }

        RemovePreset(_presetModel);
    }

    public void Save()
    {
        string name = _name.Trim();
        if (string.IsNullOrEmpty(name) || _presetModel.PresetKey == PresetSettingsModel.PRESET_KEY_CUSTOM)
        {
            return;
        }

        _presetModel.PresetName = name;
        _presetModel.AllowVariation = _allowVariation;
        SavePreset(_presetModel);

        if (_setAsDefault)
        {
            SaveDefaultPresetKey(_presetModel);
        }
    }

    public void SaveAsNew()
    {
        string name = _name.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        _presetModel.PresetKey = Guid.NewGuid().ToString();
        _presetModel.PresetName = name;
        _presetModel.AllowVariation = _allowVariation;
        SavePreset(_presetModel);

        if (_setAsDefault)
        {
            SaveDefaultPresetKey(_presetModel);
        }
    }

    public void UpdateName(string name)
    {
        _name = name;
        UpdateUI();
    }

    public void UpdateSetAsDefault(bool isDefault)
    {
        _setAsDefault = isDefault;
    }

    public void UpdateAllowVariation(bool allow)
    {
        _allowVariation = allow;
    }

    private void UpdateUI()
    {
        string name = _name.Trim();
        bool isCustomPreset = _presetModel.PresetKey == PresetSettingsModel.PRESET_KEY_CUSTOM;
        bool isNameValid = !string.IsNullOrEmpty(name);

        SaveEnabled = !isCustomPreset && isNameValid;
        SaveAsNewEnabled = isNameValid && !IsNameExisting(name);
        DeleteEnabled = !isCustomPreset;
    }

    #region Preset store

    private static void SavePreset(PresetSettingsModel preset)
    {
        switch (preset)
        {
            case PlayerPresetSettingsModel player:
                Dictionary<string, PlayerPresetSettingsModel> players = AppSettingsModel.PlayerSettingPresets;
                players[player.PresetKey] = player;
                AppSettingsModel.PlayerSettingPresets = players;
                break;
            case VideoSettingsModel video:
                Dictionary<string, VideoSettingsModel> videos = AppSettingsModel.VideoSettingPresets;
                videos[video.PresetKey] = video;
                AppSettingsModel.VideoSettingPresets = videos;
                break;
            case AudioSettingsModel audio:
                Dictionary<string, AudioSettingsModel> audios = AppSettingsModel.AudioSettingPresets;
                audios[audio.PresetKey] = audio;
                AppSettingsModel.AudioSettingPresets = audios;
                break;
            case SubtitleSettingsModel subtitle:
                Dictionary<string, SubtitleSettingsModel> subtitles = AppSettingsModel.SubtitleSettingPresets;
                subtitles[subtitle.PresetKey] = subtitle;
                AppSettingsModel.SubtitleSettingPresets = subtitles;
                break;
        }
    }

    private static void RemovePreset(PresetSettingsModel preset)
    {
        switch (preset)
        {
            case PlayerPresetSettingsModel player:
                Dictionary<string, PlayerPresetSettingsModel> players = AppSettingsModel.PlayerSettingPresets;
                if (players.Remove(player.PresetKey))
                {
                    AppSettingsModel.PlayerSettingPresets = players;
                }

                break;
            case VideoSettingsModel video:
                Dictionary<string, VideoSettingsModel> videos = AppSettingsModel.VideoSettingPresets;
                if (videos.Remove(video.PresetKey))
                {
                    AppSettingsModel.VideoSettingPresets = videos;
                }

                break;
            case AudioSettingsModel audio:
                Dictionary<string, AudioSettingsModel> audios = AppSettingsModel.AudioSettingPresets;
                if (audios.Remove(audio.PresetKey))
                {
                    AppSettingsModel.AudioSettingPresets = audios;
                }

                break;
            case SubtitleSettingsModel subtitle:
                Dictionary<string, SubtitleSettingsModel> subtitles = AppSettingsModel.SubtitleSettingPresets;
                if (subtitles.Remove(subtitle.PresetKey))
                {
                    AppSettingsModel.SubtitleSettingPresets = subtitles;
                }

                break;
        }
    }

    private static string LoadDefaultPresetKey(PresetSettingsModel preset)
    {
        return preset switch
        {
            PlayerPresetSettingsModel => AppSettingsModel.DefaultPlayerSettingPresetKey,
            AudioSettingsModel => AppSettingsModel.DefaultAudioSettingPresetKey,
            SubtitleSettingsModel => AppSettingsModel.DefaultSubtitleSettingPresetKey,
            _ => AppSettingsModel.DefaultVideoSettingPresetKey,
        };
    }

    private static void SaveDefaultPresetKey(PresetSettingsModel preset)
    {
        switch (preset)
        {
            case PlayerPresetSettingsModel:
                AppSettingsModel.DefaultPlayerSettingPresetKey = preset.PresetKey;
                break;
            case AudioSettingsModel:
                AppSettingsModel.DefaultAudioSettingPresetKey = preset.PresetKey;
                break;
            case SubtitleSettingsModel:
                AppSettingsModel.DefaultSubtitleSettingPresetKey = preset.PresetKey;
                break;
            default:
                AppSettingsModel.DefaultVideoSettingPresetKey = preset.PresetKey;
                break;
        }
    }

    private bool IsNameExisting(string name)
    {
        return _presetModel switch
        {
            PlayerPresetSettingsModel => AppSettingsModel.PlayerSettingPresets.Values.Any(preset => preset.PresetName == name),
            AudioSettingsModel => AppSettingsModel.AudioSettingPresets.Values.Any(preset => preset.PresetName == name),
            SubtitleSettingsModel => AppSettingsModel.SubtitleSettingPresets.Values.Any(preset => preset.PresetName == name),
            _ => AppSettingsModel.VideoSettingPresets.Values.Any(preset => preset.PresetName == name),
        };
    }

    #endregion
}
