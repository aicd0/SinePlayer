// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;

using SinePlayerUWP.Data.Models.Misc;

namespace SinePlayerUWP.Views.Pages.Settings;

internal partial class ImageSourceSettingsViewModel : INotifyPropertyChanged
{
    private const string TAG = nameof(ImageSourceSettingsViewModel);

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

    public void Initialize(SettingsSharedViewModel shared)
    {
        Shared = shared;
        Shared.UpdateStarted += Update;
    }

    public void SetScanOnLaunch(bool scanOnLaunch)
    {
        _scanOnLaunch = scanOnLaunch;
        AppSettingsModel.ExternalModel model = AppSettingsModel.GetModel();
        model.ScanOnLaunch = scanOnLaunch;
        AppSettingsModel.UpdateModel(model);
    }

    public void SetRemoveUnreachableMedia(bool removeUnreachableMedia)
    {
        _removeUnreachableMedia = removeUnreachableMedia;
        AppSettingsModel.ExternalModel model = AppSettingsModel.GetModel();
        model.RemoveUnreachableMedia = removeUnreachableMedia;
        AppSettingsModel.UpdateModel(model);
    }

    public void SetPromptBeforeRemovingMedia(bool promptBeforeRemovingMedia)
    {
        _promptBeforeRemovingMedia = promptBeforeRemovingMedia;
        AppSettingsModel.ExternalModel model = AppSettingsModel.GetModel();
        model.PromptBeforeRemovingMedia = promptBeforeRemovingMedia;
        AppSettingsModel.UpdateModel(model);
    }

    private void Update()
    {
        UpdateBasic();
    }

    private void UpdateBasic()
    {
        AppSettingsModel.ExternalModel model = AppSettingsModel.GetModel();
        bool scanOnLaunch = model.ScanOnLaunch;
        bool removeUnreachableMedia = model.RemoveUnreachableMedia;
        bool promptBeforeRemovingMedia = model.PromptBeforeRemovingMedia;

        ScanOnLaunch = scanOnLaunch;
        RemoveUnreachableMedia = removeUnreachableMedia;
        PromptBeforeRemovingMedia = promptBeforeRemovingMedia;
    }
}
