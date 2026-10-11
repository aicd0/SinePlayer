// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using SinePlayerUWP.Common.Misc;
using SinePlayerUWP.Common.Utils;
using SinePlayerUWP.Core.Common.DebugTools;
using SinePlayerUWP.Core.Common.Threading;
using SinePlayerUWP.Data.Tables;

namespace SinePlayerUWP.Data.Models.Comic;

internal partial class FileComicHandle(MediaType type) : ComicHandle(type)
{
    public static ComicHandle FromExternal(string filePath)
    {
        MediaType type = AppInfoProvider.IsSupportedAudioExtension(Path.GetExtension(filePath))
            ? MediaType.Audio
            : MediaType.Video;
        return new FileComicHandle(type)
        {
            Location = filePath,
            Title1 = StringUtils.DisplayNameFromFilename(Path.GetFileName(filePath)),
        };
    }

    public override bool IsEditable => !IsExternal;

    public override IReadOnlyList<string> GetFolderViewPath()
    {
        string[] pieces = Location.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0)
        {
            return pieces;
        }

        return pieces[..^1];
    }

    protected override Task<bool> MoveToLocationInternal(string newLocation)
    {
        return TaskDispatcher.LongRunningThreadPool.SubmitAsync(async () =>
        {
            string sourcePath = Location;
            string targetPath = newLocation;
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
            {
                return false;
            }

            if (!File.Exists(sourcePath))
            {
                return false;
            }

            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                return false;
            }

            string? targetParent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetParent) && !Directory.Exists(targetParent))
            {
                try
                {
                    Directory.CreateDirectory(targetParent);
                }
                catch (Exception ex)
                {
                    Logger.E($"Unable to create target parent directory '{targetParent}'", ex);
                    return false;
                }
            }

            try
            {
                File.Move(sourcePath, targetPath);
            }
            catch (Exception ex)
            {
                Logger.E($"Unable to move file", ex);
                return false;
            }

            await SetLocation(targetPath);
            return true;
        });
    }
}
