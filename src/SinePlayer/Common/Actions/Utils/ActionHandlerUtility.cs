// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Common.Actions.Providers;

namespace SinePlayer.Common.Actions.Utils;

internal class ActionHandlerUtility
{
    private ActionHandlerUtility()
    {
    }

    public static void RegisterCommonProviders(ActionHandler handler)
    {
        handler.RegisterProvider(new MessageDialogProvider());
        handler.RegisterProvider(new OpenTabProvider());
        handler.RegisterProvider(new EditMediaProvider());
        handler.RegisterProvider(new RemoveMediaProvider());
        handler.RegisterProvider(new FullscreenServiceProvider());
    }
}
