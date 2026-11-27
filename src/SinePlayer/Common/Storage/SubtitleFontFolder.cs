// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Storage;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Common.Storage;

/// <summary>
/// The fonts kept outside the media files: a folder the user imports them into, the subfolders they group them in, and
/// the ones they turned on. The paths of the turned-on files are what a subtitle chain is given, so what the settings
/// page writes and what a subtitle is drawn with are one set rather than two that have to agree.
/// </summary>
internal static class SubtitleFontFolder
{
    private const string TAG = nameof(SubtitleFontFolder);

    private static readonly IReadOnlySet<string> _supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".otc",
        ".otf",
        ".ttc",
        ".ttf",
    };
    public static IReadOnlySet<string> SupportedExtensions => _supportedExtensions;

    public static string FolderPath => Path.Combine(StorageLocation.LocalFolderPath, "Fonts");

    public static OperationResult EnsureExists()
    {
        string folderPath = FolderPath;
        try
        {
            Directory.CreateDirectory(folderPath);
        }
        catch (Exception ex)
        {
            return OperationResult.Failure($"The fonts folder could not be created: {ex.Message}");
        }

        return OperationResult.Success();
    }

    /// <summary>
    /// The fonts in the folder and in its subfolders, each with whether it is turned on, ordered by relative path. A
    /// folder that is not there holds none, and one that cannot be read is left out rather than taking the rest with it.
    /// </summary>
    public static IReadOnlyList<SubtitleFontFile> ReadFiles()
    {
        string folderPath = FolderPath;
        if (!Directory.Exists(folderPath))
        {
            return [];
        }

        HashSet<string> enabled = new(AppSettingsModel.EnabledFonts, StringComparer.OrdinalIgnoreCase);
        List<SubtitleFontFile> files = [];
        CollectFontFiles(folderPath, string.Empty, enabled, files);
        files.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return files;
    }

    /// <summary>The files the renderer is to be given, in the order they are read, which is the order of their priority.</summary>
    public static IReadOnlyList<string> ReadEnabledPaths()
    {
        List<string> paths = [];
        foreach (SubtitleFontFile file in ReadFiles())
        {
            if (file.IsEnabled)
            {
                paths.Add(file.Path);
            }
        }

        return paths;
    }

    /// <summary>
    /// Turns fonts on or off. Only the relative path is remembered, so the folder remains the record of which files there
    /// are: one that is renamed, moved or removed is simply no longer enabled.
    /// </summary>
    public static void SetEnabled(IReadOnlyList<string> relativePaths, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        if (relativePaths.Count == 0)
        {
            return;
        }

        HashSet<string> names = new(AppSettingsModel.EnabledFonts, StringComparer.OrdinalIgnoreCase);
        foreach (string relativePath in relativePaths)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            if (enabled)
            {
                names.Add(relativePath);
            }
            else
            {
                names.Remove(relativePath);
            }
        }

        AppSettingsModel.EnabledFonts = [.. names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Copies the picked files into the fonts folder itself, replacing a file of the same name.</summary>
    public static Task<OperationResult> ImportAsync(IReadOnlyList<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        return TaskDispatcher.LongRunningThreadPool.Submit(() => Import(sourcePaths));
    }

    private static OperationResult Import(IReadOnlyList<string> sourcePaths)
    {
        OperationResult created = EnsureExists();
        if (!created.IsSuccessful)
        {
            return created;
        }

        string folderPath = FolderPath;
        string? failure = null;
        foreach (string sourcePath in sourcePaths)
        {
            string name = Path.GetFileName(sourcePath);
            if (!_supportedExtensions.Contains(Path.GetExtension(name)))
            {
                continue;
            }

            string destinationPath = Path.Combine(folderPath, name);
            if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
            catch (Exception ex)
            {
                failure ??= $"'{name}' could not be imported: {ex.Message}";
            }
        }

        if (failure is not null)
        {
            return OperationResult.Failure(failure);
        }

        Logger.I(TAG, $"{sourcePaths.Count} picked file(s) were imported into '{folderPath}'");
        return OperationResult.Success();
    }

    private static void CollectFontFiles(string folderPath, string relativeFolder, HashSet<string> enabled, List<SubtitleFontFile> files)
    {
        string[] paths;
        try
        {
            paths = Directory.GetFiles(folderPath);
        }
        catch (Exception ex)
        {
            Logger.E(TAG, $"The folder '{folderPath}' could not be read", ex);
            return;
        }

        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            if (!_supportedExtensions.Contains(Path.GetExtension(name)))
            {
                continue;
            }

            string relativePath = Path.Combine(relativeFolder, name);
            files.Add(new SubtitleFontFile(relativePath, path, enabled.Contains(relativePath)));
        }

        string[] folders;
        try
        {
            folders = Directory.GetDirectories(folderPath);
        }
        catch (Exception ex)
        {
            Logger.E(TAG, $"The subfolders of '{folderPath}' could not be read", ex);
            return;
        }

        foreach (string folder in folders)
        {
            CollectFontFiles(folder, Path.Combine(relativeFolder, Path.GetFileName(folder)), enabled, files);
        }
    }
}

/// <summary>
/// One file under the fonts folder, named by its path below that folder rather than by the family it carries inside —
/// which is also what is remembered when it is turned on, since two subfolders may each hold a file of the same name.
/// </summary>
internal readonly record struct SubtitleFontFile(string RelativePath, string Path, bool IsEnabled);