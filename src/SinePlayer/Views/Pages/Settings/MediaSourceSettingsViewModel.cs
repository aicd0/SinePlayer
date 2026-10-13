// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;

using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Views.Pages.Settings;

internal partial class MediaSourceSettingsViewModel : INotifyPropertyChanged
{
    private const string TAG = nameof(MediaSourceSettingsViewModel);

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

    private bool _isRescanning = true;
    public bool IsRescanning
    {
        get => _isRescanning;
        set
        {
            _isRescanning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRescanning)));
        }
    }

    private bool _scanOnLaunch = true;
    public bool ScanOnLaunch
    {
        get => _scanOnLaunch;
        set
        {
            _scanOnLaunch = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScanOnLaunch)));
        }
    }

    private bool _removeUnreachableMedia = true;
    public bool RemoveUnreachableMedia
    {
        get => _removeUnreachableMedia;
        set
        {
            _removeUnreachableMedia = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemoveUnreachableMedia)));
        }
    }

    private bool _promptBeforeRemovingMedia = true;
    public bool PromptBeforeRemovingMedia
    {
        get => _promptBeforeRemovingMedia;
        set
        {
            _promptBeforeRemovingMedia = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PromptBeforeRemovingMedia)));
        }
    }

    private bool _autoDetectCollections = false;
    public bool AutoDetectCollections
    {
        get => _autoDetectCollections;
        set
        {
            _autoDetectCollections = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoDetectCollections)));
        }
    }

    private bool _useFolderNameAsTitle = false;
    public bool UseFolderNameAsTitle
    {
        get => _useFolderNameAsTitle;
        set
        {
            _useFolderNameAsTitle = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseFolderNameAsTitle)));
        }
    }

    public void Initialize(SettingsSharedViewModel shared)
    {
        Shared = shared;
        Shared.UpdateStarted += Update;
    }

    private void Update()
    {
        UpdateBasic();
    }

    private void UpdateBasic()
    {
        ScanOnLaunch = AppSettingsModel.ScanOnLaunch;
        RemoveUnreachableMedia = AppSettingsModel.RemoveUnreachableMedia;
        PromptBeforeRemovingMedia = AppSettingsModel.PromptBeforeRemovingMedia;
        AutoDetectCollections = AppSettingsModel.AutoDetectCollections;
        UseFolderNameAsTitle = AppSettingsModel.UseFolderNameAsTitle;
    }
}
