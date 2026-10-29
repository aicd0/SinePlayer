// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Storage;

using Windows.Storage;
using Windows.System;

namespace SinePlayer.Common.Utils;

internal static class ThirdPartyLauncher
{
    private const string TAG = nameof(ThirdPartyLauncher);

    public static async Task<OperationResult> ShowInFileExplorer(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return OperationResultExt.Failure(TAG, "Path is empty.", isFatal: true);
        }

        try
        {
            if (File.Exists(path))
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                StorageFolder folder = await file.GetParentAsync();
                FolderLauncherOptions options = new();
                options.ItemsToSelect.Add(file);
                await Launcher.LaunchFolderAsync(folder, options);
            }
            else if (Directory.Exists(path))
            {
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(path);
                await Launcher.LaunchFolderAsync(folder);
            }
            else
            {
                return OperationResultExt.Failure(TAG, $"Path does not exist: {path}");
            }
        }
        catch (Exception ex)
        {
            return OperationResultExt.Failure(TAG, ex);
        }

        return OperationResult.Success();
    }

    public static OperationResult<string> OpenTemporaryTextFile(string filename, string text)
    {
        OperationResult<string> writeErr = WriteTemporaryTextFile(filename, text);
        if (!writeErr.IsSuccessful)
        {
            return OperationResultExt.Failure<string>(writeErr);
        }

        OperationResult openErr = OpenFile(writeErr.Result);
        if (!openErr.IsSuccessful)
        {
            return OperationResultExt.Failure<string>(openErr);
        }

        return OperationResultExt.Success(writeErr.Result);
    }

    public static OperationResult OpenFile(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            return OperationResultExt.Failure(TAG, ex);
        }

        return OperationResult.Success();
    }

    public static async Task<OperationResult<string>> EditTemporaryTextFileAsync(string filename, IEnumerable<string> lines)
    {
        string text = string.Join(Environment.NewLine, lines);
        OperationResult<string> writeErr = WriteTemporaryTextFile(filename, text);
        if (!writeErr.IsSuccessful)
        {
            return OperationResultExt.Failure<string>(writeErr);
        }

        string filePath = writeErr.Result;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{filePath}\"",
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return OperationResultExt.Failure<string>(TAG, "Failed to launch notepad.");
            }

            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            return OperationResultExt.Failure<string>(TAG, ex);
        }

        return OperationResultExt.Success(filePath);
    }

    private static OperationResult<string> WriteTemporaryTextFile(string filename, string text)
    {
        string directoryPath = StorageLocation.TemporaryFolderPath;
        try
        {
            Directory.CreateDirectory(directoryPath);
        }
        catch (Exception ex)
        {
            return OperationResultExt.Failure<string>(TAG, ex);
        }

        string filePath = Path.Combine(directoryPath, filename);
        try
        {
            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            writer.Write(text);
        }
        catch (Exception ex)
        {
            return OperationResultExt.Failure<string>(TAG, ex);
        }

        return OperationResultExt.Success(filePath);
    }
}
