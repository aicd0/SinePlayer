// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.ComponentModel;

namespace SinePlayer.Views.Pages.Settings;

internal sealed partial class FontsSettingsViewModel : INotifyPropertyChanged
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

    private bool _noFonts = false;
    public bool NoFonts
    {
        get => _noFonts;
        set
        {
            _noFonts = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoFonts)));
        }
    }

    public void Initialize(SettingsSharedViewModel shared)
    {
        Shared = shared;
    }
}