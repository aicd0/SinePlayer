// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using SinePlayer.Common.Misc;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Data.Tables;

namespace SinePlayer.Data.Models.Media;

internal partial class FileMediaHandle(MediaType type) : MediaHandle(type)
{
    public static MediaHandle FromExternal(string filePath)
    {
        MediaType type = AppInfoProvider.IsSupportedAudioExtension(Path.GetExtension(filePath))
            ? MediaType.Audio
            : MediaType.Video;
        return new FileMediaHandle(type)
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
