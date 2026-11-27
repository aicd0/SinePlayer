// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Localization;
using SinePlayer.Common.Storage;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Utils;

using Windows.Storage;

namespace SinePlayer.Views.Pages.Settings;

internal sealed partial class FontsSettingsView : BaseUserControl
{
    /// <summary>The fonts that are turned on, which is what a selected node means and what is written back.</summary>
    private readonly HashSet<string> _enabled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same set as it last was on disk, so a click on a whole group writes only what it changed.</summary>
    private readonly HashSet<string> _persisted = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<TreeViewNode, string> _fontPaths = [];

    private bool _syncing;

    public FontsSettingsViewModel ViewModel { get; } = new();

    public FontsSettingsView()
    {
        InitializeComponent();
    }

    protected override void OnResume()
    {
        base.OnResume();
        UpdateFonts();
    }

    public void Initialize(SettingsSharedViewModel shared)
    {
        ViewModel.Initialize(shared);
    }

    private void ImportFontsButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            IReadOnlyList<StorageFile> files = await FilePickerUtils.PickFiles(ViewModel.Shared.WindowId, SubtitleFontFolder.SupportedExtensions);
            if (files.Count == 0)
            {
                return;
            }

            OperationResult imported = await SubtitleFontFolder.ImportAsync((List<string>)[.. files.Select(file => file.Path)]);

            // A partial import is still an import: the tree has to show what did arrive, and the error says what did not.
            UpdateFonts();
            imported.ShowErrorMessage(ViewModel.Shared.ActionHandler);
        });
    }

    private void OpenFontsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            OperationResult result = await OpenFontsFolder();
            result.ShowErrorMessage(ViewModel.Shared.ActionHandler);
        });
    }

    private static async Task<OperationResult> OpenFontsFolder()
    {
        OperationResult created = SubtitleFontFolder.EnsureExists();
        if (!created.IsSuccessful)
        {
            return created;
        }

        try
        {
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(SubtitleFontFolder.FolderPath);
            await Windows.System.Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex)
        {
            return OperationResultExt.Failure(nameof(OpenFontsFolder), ex);
        }

        return OperationResultExt.Success();
    }

    private void UpdateFonts()
    {
        IReadOnlyList<SubtitleFontFile> files = SubtitleFontFolder.ReadFiles();

        bool previous = _syncing;
        _syncing = true;
        try
        {
            FontsTreeView.SelectedNodes.Clear();
            FontsTreeView.RootNodes.Clear();
            _fontPaths.Clear();
            _enabled.Clear();
            _persisted.Clear();
            foreach (SubtitleFontFile file in files)
            {
                if (file.IsEnabled)
                {
                    _enabled.Add(file.RelativePath);
                }
            }

            _persisted.UnionWith(_enabled);

            if (files.Count > 0)
            {
                FontsTreeView.RootNodes.Add(BuildTree(files));
                SyncSelection();
            }
        }
        finally
        {
            _syncing = previous;
        }

        ViewModel.NoFonts = files.Count == 0;
    }

    /// <summary>
    /// Builds the nodes the folder describes: "Select all" over the files and subfolders of the fonts folder itself,
    /// each subfolder a group of what is under it. Only folders that hold a font get a node, so every one of them is a
    /// switch over something.
    /// </summary>
    private TreeViewNode BuildTree(IReadOnlyList<SubtitleFontFile> files)
    {
        TreeViewNode root = new()
        {
            Content = StringResourceProvider.Instance.SelectAll,
            IsExpanded = true,
        };

        Dictionary<string, TreeViewNode> folders = new(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = root,
        };

        HashSet<string> folderNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (SubtitleFontFile file in files)
        {
            for (string? folder = Path.GetDirectoryName(file.RelativePath); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            {
                folderNames.Add(folder);
            }
        }

        List<string> folderPaths = [.. folderNames];
        folderPaths.Sort((a, b) =>
        {
            int byDepth = Depth(a).CompareTo(Depth(b));
            return byDepth != 0 ? byDepth : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        foreach (string folder in folderPaths)
        {
            string parent = Path.GetDirectoryName(folder) ?? string.Empty;
            if (!folders.TryGetValue(parent, out TreeViewNode? parentNode))
            {
                continue;
            }

            TreeViewNode node = new()
            {
                Content = Path.GetFileName(folder),
                IsExpanded = true,
            };
            parentNode.Children.Add(node);
            folders[folder] = node;
        }

        foreach (SubtitleFontFile file in files)
        {
            string parent = Path.GetDirectoryName(file.RelativePath) ?? string.Empty;
            if (!folders.TryGetValue(parent, out TreeViewNode? parentNode))
            {
                continue;
            }

            TreeViewNode node = new() { Content = Path.GetFileName(file.RelativePath) };
            parentNode.Children.Add(node);
            _fontPaths[node] = file.RelativePath;
        }

        return root;
    }

    private static int Depth(string relativePath)
    {
        return relativePath.Count(character => character == Path.DirectorySeparatorChar) + 1;
    }

    private void FontsTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_syncing)
        {
            return;
        }

        // One click arrives as several items. The tree is a checkbox tree: unchecking a folder unchecks the fonts under
        // it and leaves its ancestors partial, and every one of those is reported here as removed. Taking them one at a
        // time would read the ancestors' own change as more clicks — the root turning partial looked like "Select all"
        // being switched off, which is what switched the whole list off. What the click asked for is the state the fonts
        // ended in, so that is what is read, once.
        List<string> enabled = [];
        foreach (KeyValuePair<TreeViewNode, string> pair in _fontPaths)
        {
            if (IsNodeSelected(pair.Key))
            {
                enabled.Add(pair.Value);
            }
        }

        _enabled.Clear();
        _enabled.UnionWith(enabled);
        PersistEnabled();
    }

    /// <summary>
    /// Points the tree at the enabled set: the fonts are what is chosen, and the folders and the root are what the tree
    /// makes of them — a folder is on when everything under it is, and partial when only some of it is, all of which the
    /// control derives from the fonts on its own.
    /// </summary>
    private void SyncSelection()
    {
        bool previous = _syncing;
        _syncing = true;
        try
        {
            FontsTreeView.SelectedNodes.Clear();

            foreach (KeyValuePair<TreeViewNode, string> pair in _fontPaths)
            {
                if (_enabled.Contains(pair.Value))
                {
                    FontsTreeView.SelectedNodes.Add(pair.Key);
                }
            }
        }
        finally
        {
            _syncing = previous;
        }
    }
    private bool IsNodeSelected(TreeViewNode node)
    {
        return FontsTreeView.SelectedNodes.Contains(node);
    }

    private void PersistEnabled()
    {
        List<string> added = [.. _enabled.Where(path => !_persisted.Contains(path))];
        List<string> removed = [.. _persisted.Where(path => !_enabled.Contains(path))];
        if (added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        SubtitleFontFolder.SetEnabled(added, true);
        SubtitleFontFolder.SetEnabled(removed, false);
        _persisted.Clear();
        _persisted.UnionWith(_enabled);
    }
}