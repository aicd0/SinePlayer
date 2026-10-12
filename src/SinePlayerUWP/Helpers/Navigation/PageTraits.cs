// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

#nullable disable

using System;

using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Views.Pages.Player;
using SinePlayerUWP.Views.Pages.Settings;

namespace SinePlayerUWP.Helpers.Navigation;

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
