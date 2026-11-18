// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Specialized;
using System.Threading.Tasks;

using SinePlayer.Common.Actions.Components;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Utils;
using SinePlayer.Views.AppWindows.Main;

using Windows.Storage;

namespace SinePlayer.Common.Actions.Providers;

internal class OpenFileProvider : IActionProvider
{
    public const string NAME = "OpenFile";

    public string Name => NAME;

    public async Task<ActionResult> Handle(IActionProviderContext context, NameValueCollection parameters)
    {
        IMainWindowComponent? mainWindowCom = context.GetComponent<IMainWindowComponent>();
        if (mainWindowCom is null)
        {
            return ActionResult.FromFailure("IMainWindowComponent component not found.");
        }

        StorageFile? file = await FilePickerUtils.PickFile(mainWindowCom.WindowId, AppInfoProvider.SupportedExternalFileExtensions);
        if (file is null)
        {
            return ActionResult.FromSuccess();
        }

        MainWindow? window = App.Instance.WindowManager.GetWindow(mainWindowCom.WindowId);
        if (window is null)
        {
            return ActionResult.FromFailure($"Window {mainWindowCom.WindowId} not found.");
        }

        await App.Instance.OnCommandLine(window, [file.Path]);
        return ActionResult.FromSuccess();
    }
}
