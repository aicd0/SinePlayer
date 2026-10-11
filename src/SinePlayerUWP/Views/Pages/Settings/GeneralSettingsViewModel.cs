// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

using SinePlayerUWP.Common.Localization;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Models.Misc;

namespace SinePlayerUWP.Views.Pages.Settings;

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

    private List<TapMediaBehaviorEntry> _openMediaDefaultBaheviors = [];
    public List<TapMediaBehaviorEntry> OpenMediaDefaultBaheviors
    {
        get => _openMediaDefaultBaheviors;
        set
        {
            _openMediaDefaultBaheviors = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OpenMediaDefaultBaheviors)));
        }
    }

    private int _openMediaDefaultBaheviorIndex = 0;
    public int OpenMediaDefaultBaheviorIndex
    {
        get => _openMediaDefaultBaheviorIndex;
        set
        {
            _openMediaDefaultBaheviorIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OpenMediaDefaultBaheviorIndex)));
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

    public void SetOpenMediaDefaultBehavior(int index)
    {
        if (index == _openMediaDefaultBaheviorIndex || index < 0 || index >= _openMediaDefaultBaheviors.Count)
        {
            return;
        }

        _openMediaDefaultBaheviorIndex = index;
        AppSettingsModel.OpenMediaDefaultBehavior = _openMediaDefaultBaheviors[index].Behavior;
    }

    private void Update()
    {
        UpdateCloseLastTabBehavior();
        UpdateHomePageTapMediaBehavior();
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

    private void UpdateHomePageTapMediaBehavior()
    {
        AppSettingsModel.OpenMediaBehaviorEnum behavior = AppSettingsModel.OpenMediaDefaultBehavior;
        List<TapMediaBehaviorEntry> entries = [
            new(AppSettingsModel.OpenMediaBehaviorEnum.OpenInCurrentTab),
            new(AppSettingsModel.OpenMediaBehaviorEnum.OpenInNewTab),
            new(AppSettingsModel.OpenMediaBehaviorEnum.OpenInLastActiveReaderTab),
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

        OpenMediaDefaultBaheviors = entries;
        OpenMediaDefaultBaheviorIndex = selectedIndex;
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

    public class TapMediaBehaviorEntry(AppSettingsModel.OpenMediaBehaviorEnum behavior)
    {
        public AppSettingsModel.OpenMediaBehaviorEnum Behavior => behavior;

        public string Name
        {
            get
            {
                return behavior switch
                {
                    AppSettingsModel.OpenMediaBehaviorEnum.OpenInCurrentTab => StringResourceProvider.Instance.OpenInCurrentTab,
                    AppSettingsModel.OpenMediaBehaviorEnum.OpenInNewTab => StringResourceProvider.Instance.OpenInNewTab,
                    AppSettingsModel.OpenMediaBehaviorEnum.OpenInLastActiveReaderTab => StringResourceProvider.Instance.OpenInLastActiveReaderTab,
                    _ => throw new InvalidEnumArgumentException(nameof(Behavior)),
                };
            }
        }
    }
}
