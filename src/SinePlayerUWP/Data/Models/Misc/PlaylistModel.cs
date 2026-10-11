// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using SinePlayerUWP.Core.Common.DebugTools;
using SinePlayerUWP.Data.Models.Media;

namespace SinePlayerUWP.Data.Models.Misc;

internal class PlaylistModel
{
    private const string TAG = nameof(PlaylistModel);

    public static PlaylistModel CreateEmpty()
    {
        return new([]);
    }

    public static async Task<PlaylistModel?> CreateFromSerializedString(string serializedPlaylist)
    {
        List<PlaylistItemJsonModel?>? jsonItems;
        try
        {
            jsonItems = JsonSerializer.Deserialize<List<PlaylistItemJsonModel?>>(serializedPlaylist);
        }
        catch (JsonException ex)
        {
            Logger.E(TAG, ex);
            return null;
        }

        if (jsonItems is null)
        {
            return null;
        }

        return await CreateFromBuilder(jsonItems.Where(x => x is not null).Select(x => x!));
    }

    private static async Task<PlaylistModel> CreateFromBuilder(IEnumerable<PlaylistItemJsonModel> builder)
    {
        List<long> mediaIds = [];
        foreach (PlaylistItemJsonModel jsonItem in builder)
        {
            if (!jsonItem.IsExternal.HasValue)
            {
                continue;
            }

            if (!jsonItem.IsExternal.Value)
            {
                if (!jsonItem.MediaId.HasValue)
                {
                    continue;
                }

                mediaIds.Add(jsonItem.MediaId.Value);
            }
        }

        List<MediaModel> libraryMedia = await MediaModel.BatchFromId(mediaIds);
        var mediaIdMap = new Dictionary<long, MediaModel>();
        foreach (MediaModel media in libraryMedia)
        {
            mediaIdMap[media.Id] = media;
        }

        List<PlaylistItem> items = [];
        foreach (PlaylistItemJsonModel jsonItem in builder)
        {
            if (!jsonItem.IsExternal.HasValue)
            {
                continue;
            }

            MediaModel? media = null;
            if (jsonItem.IsExternal.Value)
            {
                media = await MediaModel.FromExternalLocation(jsonItem.Location ?? string.Empty);
            }
            else if (jsonItem.MediaId.HasValue)
            {
                mediaIdMap.TryGetValue(jsonItem.MediaId.Value, out media);
            }

            if (media is not null && !media.IsCollection)
            {
                string id = string.IsNullOrEmpty(jsonItem.Id) ? Guid.NewGuid().ToString() : jsonItem.Id;
                items.Add(new()
                {
                    Media = media,
                    Id = id,
                });
            }
        }

        return new(items);
    }

    private readonly List<PlaylistItem> _items;

    public IReadOnlyList<PlaylistItem> Items => _items;

    private PlaylistModel(List<PlaylistItem> items)
    {
        _items = items;
    }

    public Builder ToBuilder()
    {
        return new Builder().AddItems(_items);
    }

    public string ToSerializedString()
    {
        IEnumerable<PlaylistItemJsonModel> jsonItems = _items
            .Select(x => new PlaylistItemJsonModel()
            {
                Id = x.Id,
                IsExternal = x.Media.IsExternal,
                MediaId = x.Media.Id,
                Location = x.Media.Location,
            });
        return JsonSerializer.Serialize(new List<PlaylistItemJsonModel>(jsonItems));
    }

    public class PlaylistItem
    {
        public required string Id { get; init; }
        public required MediaModel Media { get; init; }
    }

    private class PlaylistItemJsonModel
    {
        [JsonPropertyName("Id")]
        public string? Id { get; set; }

        [JsonPropertyName("IsExternal")]
        public bool? IsExternal { get; set; }

        [JsonPropertyName("MediaId")]
        public long? MediaId { get; set; }

        [JsonPropertyName("Location")]
        public string? Location { get; set; }
    }

    public class Builder
    {
        private readonly List<PlaylistItemJsonModel> _mediaItems = [];

        public Builder AddItems(IEnumerable<PlaylistItem> items)
        {
            foreach (PlaylistItem item in items)
            {
                if (item.Media.IsCollection)
                {
                    continue;
                }

                _mediaItems.Add(new PlaylistItemJsonModel()
                {
                    Id = item.Id,
                    IsExternal = item.Media.IsExternal,
                    Location = item.Media.Location,
                    MediaId = item.Media.Id
                });
            }

            return this;
        }

        public Builder AddMedia(IEnumerable<MediaModel> mediaItems)
        {
            foreach (MediaModel media in mediaItems)
            {
                AddMedia(media);
            }

            return this;
        }

        public string EnsureMedia(MediaModel media)
        {
            string? itemId = IdOf(media);
            if (string.IsNullOrEmpty(itemId))
            {
                var item = new PlaylistItemJsonModel()
                {
                    IsExternal = media.IsExternal,
                    Location = media.Location,
                    MediaId = media.Id
                };
                itemId = EnsureId(item);
                _mediaItems.Insert(0, item);
            }

            return itemId;
        }

        public Builder AddMedia(MediaModel media)
        {
            if (media.IsCollection)
            {
                return this;
            }

            _mediaItems.Add(new PlaylistItemJsonModel()
            {
                IsExternal = media.IsExternal,
                Location = media.Location,
                MediaId = media.Id
            });
            return this;
        }

        public Builder AddMediaLocation(string location)
        {
            _mediaItems.Add(new PlaylistItemJsonModel()
            {
                IsExternal = true,
                Location = location,
            });
            return this;
        }

        private string? IdOf(MediaModel media)
        {
            foreach (PlaylistItemJsonModel item in _mediaItems)
            {
                if (media.IsExternal != item.IsExternal)
                {
                    continue;
                }

                if (media.IsExternal)
                {
                    if (media.Location != item.Location)
                    {
                        continue;
                    }
                }
                else
                {
                    if (media.Id != item.MediaId)
                    {
                        continue;
                    }
                }

                return EnsureId(item);
            }

            return null;
        }

        public string ToSerializedString()
        {
            return JsonSerializer.Serialize(_mediaItems);
        }

        private static string EnsureId(PlaylistItemJsonModel item)
        {
            if (string.IsNullOrEmpty(item.Id))
            {
                item.Id = Guid.NewGuid().ToString();
            }

            return item.Id;
        }
    }
}
