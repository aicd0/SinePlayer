// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.SDK.Plugins.UI;
using SinePlayer.SDK.Plugins.UI.Menu;

namespace SinePlayer.SDK.Plugins.Media;

public interface IMediaMenuItemCreator
{
    IEnumerable<IMenuItem> CreateMenuItems(IWindowContext windowContext, IMediaModel primary, IEnumerable<IMediaModel> selection);
}
