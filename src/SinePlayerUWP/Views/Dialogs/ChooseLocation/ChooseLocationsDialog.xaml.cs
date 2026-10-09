// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.ObjectModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Common.Utils;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Models.Comic;
using SinePlayerUWP.Data.Models.Misc;
using SinePlayerUWP.ViewModels;

using Windows.Storage;

namespace SinePlayerUWP.Views.Dialogs.ChooseLocation;

public sealed partial class ChooseLocationsDialog : BaseContentDialog
{
    public ObservableCollection<FolderItemViewModel> FolderItemDataSource { get; set; } = [];

    public ChooseLocationsDialog()
    {
        InitializeComponent();
    }

    private void Update()
    {
        FolderItemDataSource.Clear();

        FolderItemDataSource.Add(new FolderItemViewModel
        {
            IsAddNew = true
        });

        foreach (string folder in AppSettingsModel.GetModel().ComicFolders)
        {
            FolderItemDataSource.Add(new FolderItemViewModel
            {
                Folder = folder,
                IsAddNew = false
            });
        }
    }

    private void ContentDialogPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ComicModel.RescanLibrary("ContentDialogPrimaryButtonClick");
    }

    private void ListViewLoaded(object sender, RoutedEventArgs e)
    {
        Update();
    }

    private void AddNewPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            if (!IsPrimaryButtonEnabled)
            {
                return;
            }
            IsPrimaryButtonEnabled = false;
            try
            {
                StorageFolder? folder = await FilePickerUtils.PickFolder(WindowId);
                if (folder == null)
                {
                    return;
                }
                AppSettingsModel.AddComicFolder(folder.Path);
                Update();
            }
            finally
            {
                IsPrimaryButtonEnabled = true;
            }
        });
    }

    private void RemoveFolderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPrimaryButtonEnabled)
        {
            return;
        }
        IsPrimaryButtonEnabled = false;
        try
        {
            var item = (FolderItemViewModel)((Grid)sender).DataContext;
            AppSettingsModel.RemoveComicFolder(item.Folder);
            Update();
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
        }
    }
}
