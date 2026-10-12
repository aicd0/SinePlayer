// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;

using SinePlayer.Common.Actions.Components;
using SinePlayer.Data.Models.Media;
using SinePlayer.Views.Dialogs.EditMediaInfo;

namespace SinePlayer.Common.Actions.Providers;

internal class EditMediaProvider : IActionProvider
{
    public const string NAME = "EditMedia";
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
        if (mediaItems.Count == 0)
        {
            return ActionResult.FromSuccess();
        }

        var dialog = new EditMediaInfoDialog(mediaItems);
        await dialog.ShowAsync(mainWindowCom.WindowId);
        return ActionResult.FromSuccess();
    }
}
