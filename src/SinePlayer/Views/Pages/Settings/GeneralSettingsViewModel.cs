// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Views.Pages.Settings;

internal partial class GeneralSettingsViewModel : INotifyPropertyChanged
{
    private const string TAG = nameof(GeneralSettingsViewModel);

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

    private List<CloseLastTabBehaviorEntry> _closeLastTabBehaviors = [];
    public List<CloseLastTabBehaviorEntry> CloseLastTabBehaviors
    {
        get => _closeLastTabBehaviors;
        set
        {
            _closeLastTabBehaviors = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloseLastTabBehaviors)));
        }
    }

    private int _closeLastTabBehaviorIndex = 0;
    public int CloseLastTabBehaviorIndex
    {
        get => _closeLastTabBehaviorIndex;
        set
        {
            _closeLastTabBehaviorIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloseLastTabBehaviorIndex)));
        }
    }

    private List<TapMediaBehaviorEntry> _openItemDefaultBaheviors = [];
    public List<TapMediaBehaviorEntry> OpenItemDefaultBaheviors
    {
        get => _openItemDefaultBaheviors;
        set
        {
            _openItemDefaultBaheviors = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OpenItemDefaultBaheviors)));
        }
    }

    private int _openItemDefaultBaheviorIndex = 0;
    public int OpenItemDefaultBaheviorIndex
    {
        get => _openItemDefaultBaheviorIndex;
        set
        {
            _openItemDefaultBaheviorIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OpenItemDefaultBaheviorIndex)));
        }
    }

    private bool _isClearHistoryEnabled = false;
    public bool IsClearHistoryEnabled
    {
        get => _isClearHistoryEnabled;
        set
        {
            _isClearHistoryEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsClearHistoryEnabled)));
        }
    }

    private bool _saveBrowsingHistory = false;
    public bool SaveBrowsingHistory
    {
        get => _saveBrowsingHistory;
        set
        {
            _saveBrowsingHistory = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SaveBrowsingHistory)));
        }
    }

    private bool _restoreWindowState = false;
    public bool RestoreWindowState
    {
        get => _restoreWindowState;
        set
        {
            _restoreWindowState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RestoreWindowState)));
        }
    }

    public void Initialize(SettingsSharedViewModel shared)
    {
        Shared = shared;
        Shared.UpdateStarted += Update;
    }

    public void SetCloseLastTabBehavior(int index)
    {
        if (index == _closeLastTabBehaviorIndex || index < 0 || index >= _closeLastTabBehaviors.Count)
        {
            return;
        }

        _closeLastTabBehaviorIndex = index;
        AppSettingsModel.CloseLastTabBehavior = _closeLastTabBehaviors[index].Behavior;
    }

    public void SetOpenItemDefaultBehavior(int index)
    {
        if (index == _openItemDefaultBaheviorIndex || index < 0 || index >= _openItemDefaultBaheviors.Count)
        {
            return;
        }

        _openItemDefaultBaheviorIndex = index;
        AppSettingsModel.OpenItemDefaultBehavior = _openItemDefaultBaheviors[index].Behavior;
    }

    private void Update()
    {
        UpdateCloseLastTabBehavior();
        UpdateOpenItemDefaultBahevior();
        RestoreWindowState = AppSettingsModel.RestoreWindowState;
        CoroutineUtils.Run(UpdateHistory);
    }

    private void UpdateCloseLastTabBehavior()
    {
        AppSettingsModel.CloseLastTabBehaviorEnum behavior = AppSettingsModel.CloseLastTabBehavior;
        List<CloseLastTabBehaviorEntry> entries = [
            new(AppSettingsModel.CloseLastTabBehaviorEnum.CloseWindow),
            new(AppSettingsModel.CloseLastTabBehaviorEnum.OpenHomePage),
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

        CloseLastTabBehaviors = entries;
        CloseLastTabBehaviorIndex = selectedIndex;
    }

    private void UpdateOpenItemDefaultBahevior()
    {
        AppSettingsModel.OpenItemBehaviorEnum behavior = AppSettingsModel.OpenItemDefaultBehavior;
        List<TapMediaBehaviorEntry> entries = [
            new(AppSettingsModel.OpenItemBehaviorEnum.OpenInCurrentTab),
            new(AppSettingsModel.OpenItemBehaviorEnum.OpenInNewTab),
            new(AppSettingsModel.OpenItemBehaviorEnum.OpenInLastActivePlayerTab),
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

        OpenItemDefaultBaheviors = entries;
        OpenItemDefaultBaheviorIndex = selectedIndex;
    }

    private async Task UpdateHistory()
    {
        bool hasHistory = !await MediaHistoryItemModel.IsEmptyAsync();
        bool saveBrowsingHistory = AppSettingsModel.SaveBrowsingHistory;

        IsClearHistoryEnabled = hasHistory;
        SaveBrowsingHistory = saveBrowsingHistory;
    }

    //
    // Types
    //

    public class CloseLastTabBehaviorEntry(AppSettingsModel.CloseLastTabBehaviorEnum behavior)
    {
        public AppSettingsModel.CloseLastTabBehaviorEnum Behavior => behavior;

        public string Name
        {
            get
            {
                return behavior switch
                {
                    AppSettingsModel.CloseLastTabBehaviorEnum.CloseWindow => StringResourceProvider.Instance.CloseWindow,
                    AppSettingsModel.CloseLastTabBehaviorEnum.OpenHomePage => StringResourceProvider.Instance.OpenHomePage,
                    _ => throw new InvalidEnumArgumentException(nameof(Behavior)),
                };
            }
        }
    }

    public class TapMediaBehaviorEntry(AppSettingsModel.OpenItemBehaviorEnum behavior)
    {
        public AppSettingsModel.OpenItemBehaviorEnum Behavior => behavior;

        public string Name
        {
            get
            {
                return behavior switch
                {
                    AppSettingsModel.OpenItemBehaviorEnum.OpenInCurrentTab => StringResourceProvider.Instance.OpenInCurrentTab,
                    AppSettingsModel.OpenItemBehaviorEnum.OpenInNewTab => StringResourceProvider.Instance.OpenInNewTab,
                    AppSettingsModel.OpenItemBehaviorEnum.OpenInLastActivePlayerTab => StringResourceProvider.Instance.OpenInLastActivePlayerTab,
                    _ => throw new InvalidEnumArgumentException(nameof(Behavior)),
                };
            }
        }
    }
}
