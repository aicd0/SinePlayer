// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Common.Misc;

internal class AppearanceManager
{
    public static AppearanceManager Instance { get; } = new();

    private Brush? _themeBackgroundCache = null;

    private AppearanceManager() { }

    public Brush GetThemeBackground()
    {
        Brush? backgroundBrush = _themeBackgroundCache;
        if (backgroundBrush is not null)
        {
            return backgroundBrush;
        }

        backgroundBrush = AppSettingsModel.AppBackground switch
        {
            AppSettingsModel.AppBackgroundEnum.None => (Brush)Application.Current.Resources["AppBackgroundNone"],
            _ => new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
        };

        _themeBackgroundCache = backgroundBrush;
        return backgroundBrush;
    }
}
