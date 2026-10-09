// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Common.Actions.Providers;

namespace SinePlayerUWP.Common.Actions.Utils;

internal class ActionHandlerUtility
{
    private ActionHandlerUtility()
    {
    }

    public static void RegisterCommonProviders(ActionHandler handler)
    {
        handler.RegisterProvider(new MessageDialogProvider());
        handler.RegisterProvider(new OpenTabProvider());
        handler.RegisterProvider(new EditComicProvider());
        handler.RegisterProvider(new RemoveComicProvider());
        handler.RegisterProvider(new FullscreenServiceProvider());
    }
}
