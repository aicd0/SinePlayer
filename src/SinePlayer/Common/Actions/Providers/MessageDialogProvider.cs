// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Specialized;
using System.Threading.Tasks;

using SinePlayer.Common.Actions.Components;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.Utils;
using SinePlayer.SDK.Models;

namespace SinePlayer.Common.Actions.Providers;

internal class MessageDialogProvider : IActionProvider
{
    public const string NAME = "messagedialog";
    public const string PARAM_TITLE = "title";
    public const string PARAM_MESSAGE = "message";

    public string Name => NAME;

    public async Task<ActionResult> Handle(IActionProviderContext context, NameValueCollection parameters)
    {
        IMainWindowComponent? mainWindowCom = context.GetComponent<IMainWindowComponent>();
        if (mainWindowCom is null)
        {
            return ActionResult.FromFailure("IMainWindowComponent component not found.");
        }

        string title = parameters[PARAM_TITLE] ?? "Untitled";
        string message = parameters[PARAM_MESSAGE] ?? "(no message provided)";
        DialogOptions options = new DialogOptions.Builder()
            .SetTitle(title)
            .SetContent(message)
            .Build();
        CoroutineUtils.Run(() => DialogUtils.EnqueueDialogAsync(mainWindowCom.WindowId, options));
        return ActionResult.FromSuccess();
    }
}
