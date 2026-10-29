// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Data.Models.Media;

namespace SinePlayer.Data.Models.Misc;

internal class PlayQueueModel
{
    private const string TAG = nameof(PlayQueueModel);

    public static PlayQueueModel CreateEmpty()
    {
        return new([]);
    }

    public static async Task<PlayQueueModel?> CreateFromSerializedString(string serializedPlayQueue)
    {
        List<PlayQueueItemJsonModel?>? jsonItems;
        try
        {
            jsonItems = JsonSerializer.Deserialize<List<PlayQueueItemJsonModel?>>(serializedPlayQueue);
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

    private static async Task<PlayQueueModel> CreateFromBuilder(IEnumerable<PlayQueueItemJsonModel> builder)
    {
        List<PlayQueueItemJsonModel> jsonItems = [.. builder.Where(x => x.MediaId.HasValue)];
        List<long> mediaIds = [.. jsonItems.Select(x => x.MediaId.GetValueOrDefault()).Distinct()];

        List<MediaModel> media = await MediaModel.BatchFromId(mediaIds);
        var mediaIdMap = new Dictionary<long, MediaModel>();
        foreach (MediaModel item in media)
        {
            mediaIdMap[item.Id] = item;
        }

        List<PlayQueueItem> items = [];
        foreach (PlayQueueItemJsonModel jsonItem in jsonItems)
        {
            if (!mediaIdMap.TryGetValue(jsonItem.MediaId.GetValueOrDefault(), out MediaModel? item) || item.IsCollection)
            {
                continue;
            }

            string id = string.IsNullOrEmpty(jsonItem.Id) ? Guid.NewGuid().ToString() : jsonItem.Id;
            items.Add(new()
            {
                Media = item,
                Id = id,
            });
        }

        return new(items);
    }

    private readonly List<PlayQueueItem> _items;

    public IReadOnlyList<PlayQueueItem> Items => _items;

    private PlayQueueModel(List<PlayQueueItem> items)
    {
        _items = items;
    }

    public Builder ToBuilder()
    {
        return new Builder().AddItems(_items);
    }

    public string ToSerializedString()
    {
        IEnumerable<PlayQueueItemJsonModel> jsonItems = _items
            .Select(x => new PlayQueueItemJsonModel()
            {
                Id = x.Id,
                MediaId = x.Media.Id,
            });
        return JsonSerializer.Serialize(new List<PlayQueueItemJsonModel>(jsonItems));
    }

    public class PlayQueueItem
    {
        public required string Id { get; init; }
        public required MediaModel Media { get; init; }
    }

    private class PlayQueueItemJsonModel
    {
        [JsonPropertyName("Id")]
        public string? Id { get; set; }

        [JsonPropertyName("MediaId")]
        public long? MediaId { get; set; }
    }

    public class Builder
    {
        private readonly List<PlayQueueItemJsonModel> _mediaItems = [];

        public Builder AddItems(IEnumerable<PlayQueueItem> items)
        {
            foreach (PlayQueueItem item in items)
            {
                if (item.Media.IsCollection)
                {
                    continue;
                }

                _mediaItems.Add(new PlayQueueItemJsonModel()
                {
                    Id = item.Id,
                    MediaId = item.Media.Id,
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
                var item = new PlayQueueItemJsonModel()
                {
                    MediaId = media.Id,
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

            _mediaItems.Add(new PlayQueueItemJsonModel()
            {
                MediaId = media.Id,
            });
            return this;
        }

        private string? IdOf(MediaModel media)
        {
            foreach (PlayQueueItemJsonModel item in _mediaItems)
            {
                if (media.Id != item.MediaId)
                {
                    continue;
                }

                return EnsureId(item);
            }

            return null;
        }

        public string ToSerializedString()
        {
            return JsonSerializer.Serialize(_mediaItems);
        }

        private static string EnsureId(PlayQueueItemJsonModel item)
        {
            if (string.IsNullOrEmpty(item.Id))
            {
                item.Id = Guid.NewGuid().ToString();
            }

            return item.Id;
        }
    }
}
