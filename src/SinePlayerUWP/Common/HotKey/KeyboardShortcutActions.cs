// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

using SinePlayerUWP.Common.Localization;

using Windows.System;

namespace SinePlayerUWP.Common.HotKey;

internal static class KeyboardShortcutActions
{
    public const string AddNewTab = "AddNewTab";
    public const string AddNewWindow = "AddNewWindow";
    public const string CloseTab = "CloseTab";
    public const string ExitApp = "ExitApp";
    public const string ExitFullscreen = "ExitFullscreen";
    public const string JumpToNextTab = "JumpToNextTab";
    public const string JumpToPreviousTab = "JumpToPreviousTab";
    public const string OpenDevTools = "OpenDevTools";
    public const string OpenNextItem = "OpenNextItem";
    public const string OpenPreviousItem = "OpenPreviousItem";
    public const string PlayPause = "PlayPause";
    public const string SeekBackward = "SeekBackward";
    public const string SeekForward = "SeekForward";
    public const string ToggleFullscreen = "ToggleFullscreen";
    public const string VolumeDown = "VolumeDown";
    public const string VolumeUp = "VolumeUp";

    public const string RootScope = "";
    public const string PlayerScope = "/Player";

    private static readonly List<KeyboardShortcutActionEntry> sAll =
    [
        new() { Id = AddNewTab, Name = StringResourceProvider.Instance.NewTab },
        new() { Id = AddNewWindow, Name = StringResourceProvider.Instance.NewWindow },
        new() { Id = CloseTab, Name = StringResourceProvider.Instance.CloseTab },
        new() { Id = ExitApp, Name = StringResourceProvider.Instance.Exit },
        new() { Id = ExitFullscreen, Name = StringResourceProvider.Instance.ExitFullscreen, IsInternal = true },
        new() { Id = JumpToNextTab, Name = StringResourceProvider.Instance.JumpToNextTab },
        new() { Id = JumpToPreviousTab, Name = StringResourceProvider.Instance.JumpToPreviousTab },
        new() { Id = OpenDevTools, Name = "Open dev tools", IsInternal = true },
        new() { Id = OpenNextItem, Name = StringResourceProvider.Instance.OpenNextItem, Scope = PlayerScope },
        new() { Id = OpenPreviousItem, Name = StringResourceProvider.Instance.OpenPreviousItem, Scope = PlayerScope },
        new() { Id = PlayPause, Name = StringResourceProvider.Instance.PlayPause, Scope = PlayerScope },
        new() { Id = SeekBackward, Name = StringResourceProvider.Instance.SeekBackward, Scope = PlayerScope },
        new() { Id = SeekForward, Name = StringResourceProvider.Instance.SeekForward, Scope = PlayerScope },
        new() { Id = ToggleFullscreen, Name = StringResourceProvider.Instance.ToggleFullscreen },
        new() { Id = VolumeDown, Name = StringResourceProvider.Instance.VolumeDown, Scope = PlayerScope },
        new() { Id = VolumeUp, Name = StringResourceProvider.Instance.VolumeUp, Scope = PlayerScope },
    ];

    private static readonly IReadOnlyList<KeyboardShortcutModel> sDefaultShortcuts =
    [
        CreateShortcut(AddNewTab, VirtualKey.T, VirtualKeyModifiers.Control),
        CreateShortcut(CloseTab, VirtualKey.W, VirtualKeyModifiers.Control),
        CreateShortcut(JumpToNextTab, VirtualKey.Tab, VirtualKeyModifiers.Control),
        CreateShortcut(JumpToPreviousTab, VirtualKey.Tab, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
        CreateShortcut(OpenNextItem, VirtualKey.Down, VirtualKeyModifiers.Control),
        CreateShortcut(OpenPreviousItem, VirtualKey.Up, VirtualKeyModifiers.Control),
        CreateShortcut(PlayPause, VirtualKey.Space),
        CreateShortcut(SeekBackward, VirtualKey.Left),
        CreateShortcut(SeekForward, VirtualKey.Right),
        CreateShortcut(ToggleFullscreen, VirtualKey.F, VirtualKeyModifiers.Control),
        CreateShortcut(VolumeDown, VirtualKey.Down),
        CreateShortcut(VolumeUp, VirtualKey.Up),
    ];
    public static IReadOnlyList<KeyboardShortcutModel> DefaultShortcuts => sDefaultShortcuts;

    private static readonly IReadOnlyList<KeyboardShortcutModel> sInternalShortcuts =
    [
        CreateShortcut(ExitFullscreen, VirtualKey.Escape),
        CreateShortcut(OpenDevTools, VirtualKey.F11, VirtualKeyModifiers.Control),
    ];
    public static IReadOnlyList<KeyboardShortcutModel> InternalShortcuts => sInternalShortcuts;

    public static List<KeyboardShortcutActionEntry> GetAllEntries()
    {
        List<KeyboardShortcutActionEntry> entries = [];
        foreach (KeyboardShortcutActionEntry entry in sAll)
        {
            if (entry.IsInternal)
            {
                continue;
            }

            entries.Add(entry);
        }

        return entries;
    }

    public static KeyboardShortcutActionEntry? Get(string action)
    {
        foreach (KeyboardShortcutActionEntry entry in sAll)
        {
            if (entry.Id == action)
            {
                return entry;
            }
        }

        return null;
    }

    private static KeyboardShortcutModel CreateShortcut(string action, VirtualKey key, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
    {
        return new KeyboardShortcutModel
        {
            Action = action,
            Key = key,
            Modifiers = modifiers,
        };
    }
}

internal sealed class KeyboardShortcutActionEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Scope { get; init; } = KeyboardShortcutActions.RootScope;

    public bool IsInternal { get; init; }
}
