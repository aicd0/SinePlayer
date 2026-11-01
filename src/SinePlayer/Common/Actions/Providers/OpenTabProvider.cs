// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Specialized;
using System.Globalization;
using System.Threading.Tasks;

using SinePlayer.Common.Actions.Components;
using SinePlayer.Views.AppWindows.Main;

namespace SinePlayer.Common.Actions.Providers;

internal class OpenTabProvider : IActionProvider
{
    public const string NAME = "OpenTab";
    public const string PARAM_URL = "URL";
    public const string PARAM_WINDOW_ID = "WindowId";
    public const string PARAM_TAB_ID = "TabId";

    public const string TAB_ID_NEW = "-1";
    public const int WINDOW_ID_NEW = -1;

    public static string ResolveTabId(MainWindow window, string? tabId)
    {
        return string.IsNullOrEmpty(tabId) ? window.CurrentTab?.Id ?? string.Empty
            : tabId == TAB_ID_NEW ? string.Empty
            : tabId;
    }

    public string Name => NAME;

    public async Task<ActionResult> Handle(IActionProviderContext context, NameValueCollection parameters)
    {
        string url = parameters[PARAM_URL] ?? string.Empty;
        if (string.IsNullOrEmpty(url))
        {
            return ActionResult.FromFailure("URL missing.");
        }

        string windowIdString = parameters[PARAM_WINDOW_ID] ?? string.Empty;
        int windowId;
        if (string.IsNullOrEmpty(windowIdString))
        {
            IMainWindowComponent? mainWindowCom = context.GetComponent<IMainWindowComponent>();
            if (mainWindowCom is null)
            {
                return ActionResult.FromFailure("IMainWindowComponent component not found.");
            }

            windowId = mainWindowCom.WindowId;
        }
        else if (!int.TryParse(windowIdString, NumberStyles.Integer, CultureInfo.InvariantCulture, out windowId) || windowId < WINDOW_ID_NEW)
        {
            return ActionResult.FromFailure($"'{windowIdString}' is not a valid window ID.");
        }

        string? tabId = parameters[PARAM_TAB_ID];

        if (windowId == WINDOW_ID_NEW)
        {
            if (tabId == TAB_ID_NEW)
            {
                return ActionResult.FromFailure("A new window cannot be asked for a new tab as well: it opens with one.");
            }

            MainWindow.Open(url);
        }
        else
        {
            MainWindow? window = App.Instance.WindowManager.GetWindow(windowId);
            if (window is null)
            {
                return ActionResult.FromFailure($"Window {windowId} not found.");
            }

            IMainPageComponent? mainPageCom = context.GetComponent<IMainPageComponent>();
            string initiateTabId = mainPageCom?.TabId ?? string.Empty;
            window.OpenTab(url, ResolveTabId(window, tabId), initiateTabId);
        }

        return ActionResult.FromSuccess();
    }
}
