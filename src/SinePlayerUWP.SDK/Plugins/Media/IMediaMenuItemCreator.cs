// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.SDK.Plugins.UI;
using SinePlayerUWP.SDK.Plugins.UI.Menu;

namespace SinePlayerUWP.SDK.Plugins.Media;

public interface IMediaMenuItemCreator
{
    IEnumerable<IMenuItem> CreateMenuItems(IWindowContext windowContext, IMediaModel primary, IEnumerable<IMediaModel> selection);
}
