// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using SinePlayer.Common.Misc;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Data.Models.Media;

namespace SinePlayer.Helpers.Misc;

internal static class FilePlayQueue
{
    private const string TAG = nameof(FilePlayQueue);

    public static async Task<IReadOnlyList<MediaModel>?> ForFolderAsync(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath))
        {
            return null;
        }

        IReadOnlyList<string>? paths = await TaskDispatcher.DefaultThreadPool.Submit<IReadOnlyList<string>?>(() =>
        {
            try
            {
                return ListMediaFiles(folderPath);
            }
            catch (Exception exception)
            {
                Logger.W(TAG, $"The folder '{folderPath}' could not be listed, so its files were left out: {exception.Message}");
                return null;
            }
        });

        if (paths is null)
        {
            return null;
        }

        return await CreateMediaAsync(paths);
    }

    public static async Task<IReadOnlyList<MediaModel>?> ForFilesAsync(IEnumerable<string> paths)
    {
        return await CreateMediaAsync(paths);
    }

    private static List<string> ListMediaFiles(string folderPath)
    {
        List<string> paths = [];
        foreach (string path in Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (AppInfoProvider.IsSupportedExternalFileExtension(Path.GetExtension(path)))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private static async Task<IReadOnlyList<MediaModel>?> CreateMediaAsync(IEnumerable<string> paths)
    {
        List<string> candidates = [];
        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                Logger.W(TAG, $"'{path}' does not exist, so it was left out of the queue.");
                continue;
            }

            candidates.Add(path);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        List<MediaModel> mediaItems = await MediaModel.FromFiles(candidates);
        if (mediaItems.Count == 0)
        {
            return null;
        }

        return [.. mediaItems.OrderBy(x => StringUtils.SmartFileNameKeySelector(x.Title), StringUtils.SmartFileNameComparer)];
    }
}
