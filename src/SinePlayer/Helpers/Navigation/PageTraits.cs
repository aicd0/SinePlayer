// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

#nullable disable

using System;

using SinePlayer.Common.BaseUI;
using SinePlayer.Views.Pages.Player;
using SinePlayer.Views.Pages.Settings;

namespace SinePlayer.Helpers.Navigation;

internal class DefaultPageTrait(Type pageType) : IPageTrait
{
    private readonly Type _pageType = pageType;

    public Type PageType => _pageType;

    public bool IsImmersiveMode => false;

    public bool AllowMultiplePages => true;
}

internal class PlayerPageTrait : IPageTrait
{
    private static IPageTrait _instance;
    public static IPageTrait Instance
    {
        get
        {
            _instance ??= new PlayerPageTrait();
            return _instance;
        }
    }

    private PlayerPageTrait() { }

    public Type PageType => typeof(PlayerPage);

    public bool IsImmersiveMode => true;

    public bool AllowMultiplePages => true;
}

internal class SettingsPageTrait : IPageTrait
{
    private static IPageTrait _instance;
    public static IPageTrait Instance
    {
        get
        {
            _instance ??= new SettingsPageTrait();
            return _instance;
        }
    }

    private SettingsPageTrait() { }

    public Type PageType => typeof(SettingsPage);

    public bool IsImmersiveMode => false;

    public bool AllowMultiplePages => false;
}
