// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayerUWP.Common.Constants;

internal static class RegistryNames
{
    //
    // Main Registry
    //

    public const string GLOBAL = "/Global";
    public const string SEARCH_HISTORY = $"{GLOBAL}/SearchHistory";
    public const string SETTINGS = $"{GLOBAL}/Settings";
    public const string MEDIA_IMPORT_EXCLUSIONS = $"{SETTINGS}/MediaImportExclusions";
    public const string STATE = $"{GLOBAL}/State";
    public const string READER_STATE = $"{STATE}/Reader";
    public const string PLUGINS = $"{GLOBAL}/Plugins";
    public const string TIPS = $"{GLOBAL}/Tips";
    public const string RESOURCES = "/Resources";
    public const string RUNTIME_RESOURCES = $"{RESOURCES}/Runtime";
    public const string PLAYLISTS = $"{RUNTIME_RESOURCES}/Playlists";
    public const string TAB_RESOURCES = $"{RESOURCES}/Tabs";

    public static class SettingsKey
    {
        public const string READER_OVERLAY_PINNED = "ReaderOverlayPinned";
        public const string SHOW_INSTALL_PLUGIN_WARNING = "ShowInstallPluginWarning";
    }

    public static class StateKey
    {
        public const string WINDOW_STATUS = "WindowStatus";
        public const string LOG_STARTED = "LogStarted";
        public const string SIDE_PANE_LAST_FILTER_PRESET = "SidePaneLastFilterPreset";
    }

    public static class PluginsKey
    {
        public const string DISABLED_PLUGINS = "DisabledPlugins";
    }

    public static class TipsKey
    {
        public const string READER_TIP_SHOWN = "ReaderTipShown";
    }
}
