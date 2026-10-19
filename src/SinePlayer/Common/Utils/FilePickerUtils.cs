// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using SinePlayer.Views.AppWindows.Main;

using Windows.Storage;
using Windows.Storage.Pickers;

namespace SinePlayer.Common.Utils;

internal static class FilePickerUtils
{
    public static async Task<StorageFolder?> PickFolder(int windowId)
    {
        MainWindow? window = App.Instance.WindowManager.GetWindow(windowId);
        if (window is null)
        {
            return null;
        }

        FolderPicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window.WindowHandle);
        picker.FileTypeFilter.Add("*");
        return await picker.PickSingleFolderAsync();
    }

    public static async Task<StorageFile?> PickFile(int windowId, IList<string> typeFilter)
    {
        MainWindow? window = App.Instance.WindowManager.GetWindow(windowId);
        if (window is null)
        {
            return null;
        }

        FileOpenPicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window.WindowHandle);

        foreach (string type in typeFilter)
        {
            picker.FileTypeFilter.Add(type);
        }

        return await picker.PickSingleFileAsync();
    }
}
