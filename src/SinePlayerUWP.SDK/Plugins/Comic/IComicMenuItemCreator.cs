// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.SDK.Plugins.UI;
using SinePlayerUWP.SDK.Plugins.UI.Menu;

namespace SinePlayerUWP.SDK.Plugins.Comic;

public interface IComicMenuItemCreator
{
    IEnumerable<IMenuItem> CreateMenuItems(IWindowContext windowContext, IComicModel primary, IEnumerable<IComicModel> selection);
}
