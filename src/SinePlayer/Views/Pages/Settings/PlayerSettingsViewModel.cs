// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.ComponentModel;

using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Views.Pages.Settings;

internal partial class PlayerSettingsViewModel : INotifyPropertyChanged
{
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

    private bool _restoreLastPlaybackPosition;
    public bool RestoreLastPlaybackPosition
    {
        get => _restoreLastPlaybackPosition;
        set
        {
            _restoreLastPlaybackPosition = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RestoreLastPlaybackPosition)));
        }
    }

    private bool _autoToggleOverlaysOnCursor;
    public bool AutoToggleOverlaysOnCursor
    {
        get => _autoToggleOverlaysOnCursor;
        set
        {
            _autoToggleOverlaysOnCursor = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoToggleOverlaysOnCursor)));
        }
    }

    private bool _autoPlayNext;
    public bool AutoPlayNext
    {
        get => _autoPlayNext;
        set
        {
            _autoPlayNext = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoPlayNext)));
        }
    }

    private bool _autoPlayOnOpen;
    public bool AutoPlayOnOpen
    {
        get => _autoPlayOnOpen;
        set
        {
            _autoPlayOnOpen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoPlayOnOpen)));
        }
    }

    private bool _autoPlayOnOpenExceptAppLaunch;
    public bool AutoPlayOnOpenExceptAppLaunch
    {
        get => _autoPlayOnOpenExceptAppLaunch;
        set
        {
            _autoPlayOnOpenExceptAppLaunch = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoPlayOnOpenExceptAppLaunch)));
        }
    }

    private bool _pauseOnLeaveTab;
    public bool PauseOnLeaveTab
    {
        get => _pauseOnLeaveTab;
        set
        {
            _pauseOnLeaveTab = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PauseOnLeaveTab)));
        }
    }

    private bool _preciseSeeking;
    public bool PreciseSeeking
    {
        get => _preciseSeeking;
        set
        {
            _preciseSeeking = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreciseSeeking)));
        }
    }

    private List<KeepScreenOnBehaviorEntry> _keepScreenOnBehaviors = [];
    public List<KeepScreenOnBehaviorEntry> KeepScreenOnBehaviors
    {
        get => _keepScreenOnBehaviors;
        set
        {
            _keepScreenOnBehaviors = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeepScreenOnBehaviors)));
        }
    }

    private int _keepScreenOnBehaviorIndex;
    public int KeepScreenOnBehaviorIndex
    {
        get => _keepScreenOnBehaviorIndex;
        set
        {
            _keepScreenOnBehaviorIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeepScreenOnBehaviorIndex)));
        }
    }

    public void Initialize(SettingsSharedViewModel shared)
    {
        Shared = shared;
        Shared.UpdateStarted += Update;
    }

    private void Update()
    {
        UpdateBasicPlayerSettings();
        UpdateKeepScreenOnBehavior();
    }

    private void UpdateBasicPlayerSettings()
    {
        CoroutineUtils.RunInMainThread(() =>
        {
            RestoreLastPlaybackPosition = AppSettingsModel.RestoreLastPlaybackPosition;
            AutoToggleOverlaysOnCursor = AppSettingsModel.AutoToggleOverlaysOnCursor;
            AutoPlayNext = AppSettingsModel.AutoPlayNext;
            AutoPlayOnOpen = AppSettingsModel.AutoPlayOnOpen;
            AutoPlayOnOpenExceptAppLaunch = AppSettingsModel.AutoPlayOnOpenExceptAppLaunch;
            PauseOnLeaveTab = AppSettingsModel.PauseOnLeaveTab;
            PreciseSeeking = AppSettingsModel.PreciseSeeking;
        });
    }

    private void UpdateKeepScreenOnBehavior()
    {
        AppSettingsModel.KeepScreenOnBehaviorEnum behavior = AppSettingsModel.KeepScreenOnBehavior;
        List<KeepScreenOnBehaviorEntry> entries = [
            new(AppSettingsModel.KeepScreenOnBehaviorEnum.Never),
            new(AppSettingsModel.KeepScreenOnBehaviorEnum.WhilePlayingVideos),
            new(AppSettingsModel.KeepScreenOnBehaviorEnum.WhilePlaying),
            new(AppSettingsModel.KeepScreenOnBehaviorEnum.Always),
        ];
        int selectedIndex = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (behavior == entries[i].Behavior)
            {
                selectedIndex = i;
                break;
            }
        }

        if (selectedIndex < 0)
        {
            selectedIndex = 0;
        }

        KeepScreenOnBehaviors = entries;
        KeepScreenOnBehaviorIndex = selectedIndex;
    }

    //
    // Types
    //

    public class KeepScreenOnBehaviorEntry(AppSettingsModel.KeepScreenOnBehaviorEnum behavior)
    {
        public AppSettingsModel.KeepScreenOnBehaviorEnum Behavior => behavior;

        public string Name
        {
            get
            {
                return behavior switch
                {
                    AppSettingsModel.KeepScreenOnBehaviorEnum.Never => StringResourceProvider.Instance.Never,
                    AppSettingsModel.KeepScreenOnBehaviorEnum.Always => StringResourceProvider.Instance.Always,
                    AppSettingsModel.KeepScreenOnBehaviorEnum.WhilePlaying => StringResourceProvider.Instance.WhilePlaying,
                    AppSettingsModel.KeepScreenOnBehaviorEnum.WhilePlayingVideos => StringResourceProvider.Instance.WhilePlayingVideos,
                    _ => throw new InvalidEnumArgumentException(nameof(Behavior)),
                };
            }
        }
    }
}
