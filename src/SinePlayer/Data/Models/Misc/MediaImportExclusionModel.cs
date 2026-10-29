// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SinePlayer.Common.Constants;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Data.Database;
using SinePlayer.Data.Models.Media;
using SinePlayer.SDK.Models;

namespace SinePlayer.Data.Models.Misc;

internal class MediaImportExclusionModel
{
    private const string TAG = nameof(MediaImportExclusionModel);

    public static MediaImportExclusionModel Instance { get; } = new();

    private readonly ConcurrentDictionary<string, string> _excluded = [];

    private readonly Lazy<IRegistryKey> _registryKey;

    private MediaImportExclusionModel()
    {
        _registryKey = new(() =>
        {
            IRegistryKey registryKey = AppDB.MainRegistry.CreateKey(RegistryNames.MEDIA_IMPORT_EXCLUSIONS);
            foreach (string location in registryKey.Keys)
            {
                _excluded[location] = string.Empty;
            }

            return registryKey;
        });
    }

    public bool Contains(string location)
    {
        EnsureInitialized();
        return _excluded.ContainsKey(location);
    }

    public void Add(IEnumerable<MediaModel> mediaItems)
    {
        EnsureInitialized();

        IRegistryKey registryKey = _registryKey.Value;

        foreach (MediaModel media in mediaItems)
        {
            if (media.IsCollection)
            {
                continue;
            }

            string location = media.Location;
            if (string.IsNullOrEmpty(location))
            {
                Logger.F(TAG, "Media location is empty.");
                continue;
            }

            _excluded[location] = string.Empty;
            registryKey.Set(location, string.Empty);
        }
    }

    public async Task<OperationResult> EditWithNotepad()
    {
        EnsureInitialized();

        string[] lines = [.. _excluded.Keys.OrderBy(location => location, StringComparer.Ordinal)];

        OperationResult<string> editErr = await ThirdPartyLauncher.EditTemporaryTextFileAsync("MediaImportExclusions.txt", lines);
        if (!editErr.IsSuccessful)
        {
            return OperationResultExt.Failure(editErr);
        }

        string filePath = editErr.Result;
        List<string> locations = [];
        try
        {
            string[] resultLines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
            foreach (string line in resultLines)
            {
                string location = line.Trim();
                if (string.IsNullOrEmpty(location))
                {
                    continue;
                }

                locations.Add(location);
            }
        }
        catch (Exception ex)
        {
            return OperationResultExt.Failure(TAG, ex);
        }

        Refresh(locations);

        return OperationResult.Success();
    }

    private void Refresh(IEnumerable<string> locations)
    {
        HashSet<string> newLocations = [.. locations];

        foreach (string location in _excluded.Keys)
        {
            if (newLocations.Contains(location))
            {
                continue;
            }

            _excluded.TryRemove(location, out _);
            _registryKey.Value.Remove(location);
        }

        foreach (string location in newLocations)
        {
            if (_excluded.ContainsKey(location))
            {
                continue;
            }

            _excluded[location] = string.Empty;
            _registryKey.Value.Set(location, string.Empty);
        }
    }

    private void EnsureInitialized()
    {
        _ = _registryKey.Value;
    }
}
