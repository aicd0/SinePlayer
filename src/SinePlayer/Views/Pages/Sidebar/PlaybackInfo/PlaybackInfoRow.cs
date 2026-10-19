// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Views.Pages.Sidebar.PlaybackInfo;

internal sealed class PlaybackInfoRow
{
    private PlaybackInfoRow(string key, string name, string value, bool isGroup)
    {
        Key = key;
        Name = name;
        Value = value;
        IsGroup = isGroup;
    }

    public string Key { get; }

    public string Name { get; }

    public string Value { get; }

    public bool IsGroup { get; }

    public static PlaybackInfoRow Group(string key, string name)
    {
        return new PlaybackInfoRow(key, name, string.Empty, true);
    }

    public static PlaybackInfoRow Property(string key, string name, string value)
    {
        return new PlaybackInfoRow(key, name, value, false);
    }
}
