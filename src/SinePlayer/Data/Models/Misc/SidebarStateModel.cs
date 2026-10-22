// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using SinePlayer.Common.Constants;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Database;
using SinePlayer.SDK.Models;

namespace SinePlayer.Data.Models.Misc;

internal class SidebarStateModel
{
    private const int SAVE_DELAY_MILLISECONDS = 500;

    private static readonly Lazy<IRegistryKey> _registryKey = new(() => AppDB.MainRegistry.CreateKey(RegistryNames.STATE));

    private static readonly Lock _pendingLock = new();
    private static SidebarStateModel? _pendingDefaults = null;
    private static bool _isSavingDefaultsScheduled = false;

    public static SidebarStateModel LoadDefaults()
    {
        Flush();

        IRegistryKey registryKey = _registryKey.Value;
        return new()
        {
            IsOpen = registryKey.GetValueOrDefault(RegistryNames.StateKey.SIDEBAR_OPEN, false),
            IsPinned = registryKey.GetValueOrDefault(RegistryNames.StateKey.SIDEBAR_PINNED, false),
            Width = registryKey.GetValueOrDefault(RegistryNames.StateKey.SIDEBAR_WIDTH, 0.0),
            SelectedItem = registryKey.GetValueOrDefault(RegistryNames.StateKey.SIDEBAR_SELECTED_ITEM, string.Empty),
        };
    }

    public static void SaveDefaults(SidebarStateModel state)
    {
        lock (_pendingLock)
        {
            _pendingDefaults = state;
            if (_isSavingDefaultsScheduled)
            {
                return;
            }

            _isSavingDefaultsScheduled = true;
        }

        CoroutineUtils.Run(async () =>
        {
            await Task.Delay(SAVE_DELAY_MILLISECONDS);
            Flush();
        });
    }

    public static void Flush()
    {
        SidebarStateModel? state;
        lock (_pendingLock)
        {
            state = _pendingDefaults;
            _pendingDefaults = null;
            _isSavingDefaultsScheduled = false;
        }

        if (state is null)
        {
            return;
        }

        IRegistryKey registryKey = _registryKey.Value;
        registryKey.Set(RegistryNames.StateKey.SIDEBAR_OPEN, state.IsOpen);
        registryKey.Set(RegistryNames.StateKey.SIDEBAR_PINNED, state.IsPinned);
        registryKey.Set(RegistryNames.StateKey.SIDEBAR_WIDTH, state.Width);
        registryKey.Set(RegistryNames.StateKey.SIDEBAR_SELECTED_ITEM, state.SelectedItem ?? string.Empty);
    }

    [JsonPropertyName("IsOpen")]
    public bool IsOpen { get; set; }

    [JsonPropertyName("IsPinned")]
    public bool IsPinned { get; set; }

    [JsonPropertyName("Width")]
    public double Width { get; set; }

    [JsonPropertyName("SelectedItem")]
    public string? SelectedItem { get; set; }
}
