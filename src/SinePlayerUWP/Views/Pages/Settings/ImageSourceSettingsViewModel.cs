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

    private bool _removeUnreachableComics = true;
    public bool RemoveUnreachableComics
    {
        get => _removeUnreachableComics;
        set
        {
            _removeUnreachableComics = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemoveUnreachableComics)));
        }
    }

    private bool _promptBeforeRemovingComics = true;
    public bool PromptBeforeRemovingComics
    {
        get => _promptBeforeRemovingComics;
        set
        {
            _promptBeforeRemovingComics = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PromptBeforeRemovingComics)));
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

    public void SetRemoveUnreachableComics(bool removeUnreachableComics)
    {
        _removeUnreachableComics = removeUnreachableComics;
        AppSettingsModel.ExternalModel model = AppSettingsModel.GetModel();
        model.RemoveUnreachableComics = removeUnreachableComics;
        AppSettingsModel.UpdateModel(model);
    }

    public void SetPromptBeforeRemovingComics(bool promptBeforeRemovingComics)
    {
        _promptBeforeRemovingComics = promptBeforeRemovingComics;
        AppSettingsModel.ExternalModel model = AppSettingsModel.GetModel();
        model.PromptBeforeRemovingComics = promptBeforeRemovingComics;
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
        bool removeUnreachableComics = model.RemoveUnreachableComics;
        bool promptBeforeRemovingComics = model.PromptBeforeRemovingComics;

        ScanOnLaunch = scanOnLaunch;
        RemoveUnreachableComics = removeUnreachableComics;
        PromptBeforeRemovingComics = promptBeforeRemovingComics;
    }
}
