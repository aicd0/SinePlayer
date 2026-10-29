// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;

using SinePlayer.Common.Actions.Components;
using SinePlayer.Common.Localization;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.SDK.Models;

namespace SinePlayer.Common.Actions.Providers;

internal class RemoveMediaProvider : IActionProvider
{
    public const string NAME = "DeleteMedia";
    public const string PARAM_MEDIA_ID = "MediaId";

    public string Name => NAME;

    public async Task<ActionResult> Handle(IActionProviderContext context, NameValueCollection parameters)
    {
        IMainWindowComponent? mainWindowCom = context.GetComponent<IMainWindowComponent>();
        if (mainWindowCom is null)
        {
            return ActionResult.FromFailure("IMainWindowComponent component not found.");
        }

        string idList = parameters[PARAM_MEDIA_ID] ?? string.Empty;
        string[] idsRaw = idList.Split(',', StringSplitOptions.RemoveEmptyEntries);
        List<long> ids = [];
        foreach (string idRaw in idsRaw)
        {
            if (!long.TryParse(idRaw, out long id))
            {
                return ActionResult.FromFailure($"Invalid MediaId: {idRaw}");
            }

            ids.Add(id);
        }

        if (ids.Count == 0)
        {
            return ActionResult.FromFailure("No valid media ID found.");
        }

        List<MediaModel> mediaItems = await MediaModel.BatchFromId(ids);

        mediaItems = [.. mediaItems.Where(x => !x.IsExternal)];
        if (mediaItems.Count == 0)
        {
            return ActionResult.FromSuccess();
        }

        string promptContent = StringResourceProvider.Instance.RemoveItemsConfirmation
            .Replace("$count", mediaItems.Count.ToString())
            .Replace("$items", string.Join('\n', mediaItems.Select(x => x.IsCollection ? x.Title : x.Location)));
        promptContent += $"\n\n{StringResourceProvider.Instance.MediaExclusionExplaination}";
        DialogOptions options = new DialogOptions.Builder()
            .SetTitle(StringResourceProvider.Instance.Warning)
            .SetContent(promptContent)
            .SetPrimaryButtonText(StringResourceProvider.Instance.RemoveAndExclude)
            .SetSecondaryButtonText(StringResourceProvider.Instance.Remove)
            .SetCloseButtonText(StringResourceProvider.Instance.Cancel)
            .Build();
        DialogResult result = await DialogUtils.EnqueueDialogAsync(mainWindowCom.WindowId, options);

        if (result != DialogResult.Primary && result != DialogResult.Secondary)
        {
            return ActionResult.FromFailure("Cancelled by user.");
        }

        if (result == DialogResult.Primary)
        {
            MediaImportExclusionModel.Instance.Add(mediaItems);
        }

        await BusyStateManager.WithBusyState(() => MediaModel.RemoveMedia(mediaItems));
        return ActionResult.FromSuccess();
    }
}
