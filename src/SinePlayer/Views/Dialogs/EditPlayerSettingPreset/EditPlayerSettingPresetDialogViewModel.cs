// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.ComponentModel;

using SinePlayer.Common.Localization;
using SinePlayer.Data.Models.Media;
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

    private MediaModel? _media = null;
    private PlayerSettingsModel _presetModel = PlayerSettingsModel.FromDefault();

    public void Initialize(MediaModel media)
    {
        _media = media;
        _presetModel = PlayerSettingsModel.LoadFromMedia(media);

        Title = StringResourceProvider.Instance.EditPreset;
        Name = _presetModel.PresetName;
        SetAsDefault = _presetModel.PresetKey == AppSettingsModel.DefaultPlayerSettingPresetKey;
        UpdateUI();
    }

    public void Delete()
    {
        if (_presetModel.PresetKey == PlayerSettingsModel.PRESET_KEY_CUSTOM)
        {
            return;
        }

        Dictionary<string, PlayerSettingsModel> presets = AppSettingsModel.PlayerSettingPresets;
        presets.Remove(_presetModel.PresetKey);
        AppSettingsModel.PlayerSettingPresets = presets;

        if (_media is not null)
        {
            _media.SetExt(MediaExt.PLAYER_SETTING_PRESET_KEY, null);
            _media.SetExt(MediaExt.CUSTOM_PLAYER_SETTINGS, null);
        }
    }

    public void Save()
    {
        string name = _name.Trim();
        if (string.IsNullOrEmpty(name) || _presetModel.PresetKey == PlayerSettingsModel.PRESET_KEY_CUSTOM)
        {
            return;
        }

        _presetModel.PresetName = name;
        Dictionary<string, PlayerSettingsModel> presets = AppSettingsModel.PlayerSettingPresets;
        presets[_presetModel.PresetKey] = _presetModel;
        AppSettingsModel.PlayerSettingPresets = presets;

        if (_setAsDefault)
        {
            AppSettingsModel.DefaultPlayerSettingPresetKey = _presetModel.PresetKey;
        }

        if (_media is not null)
        {
            _presetModel.SaveToMedia(_media);
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
        Dictionary<string, PlayerSettingsModel> presets = AppSettingsModel.PlayerSettingPresets;
        presets[_presetModel.PresetKey] = _presetModel;
        AppSettingsModel.PlayerSettingPresets = presets;

        if (_setAsDefault)
        {
            AppSettingsModel.DefaultPlayerSettingPresetKey = _presetModel.PresetKey;
        }

        if (_media is not null)
        {
            _presetModel.SaveToMedia(_media);
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

    private void UpdateUI()
    {
        string name = _name.Trim();
        bool isCustomPreset = _presetModel.PresetKey == PlayerSettingsModel.PRESET_KEY_CUSTOM;
        bool isNameValid = !string.IsNullOrEmpty(name);
        bool isNameExisting = false;
        foreach (PlayerSettingsModel preset in AppSettingsModel.PlayerSettingPresets.Values)
        {
            if (preset.PresetName == name)
            {
                isNameExisting = true;
                break;
            }
        }

        SaveEnabled = !isCustomPreset && isNameValid;
        SaveAsNewEnabled = isNameValid && !isNameExisting;
        DeleteEnabled = !isCustomPreset;
    }
}
