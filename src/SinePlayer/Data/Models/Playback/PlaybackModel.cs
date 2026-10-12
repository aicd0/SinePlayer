// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Data.Models.Playback;

internal class PlaybackModel
{
    private const string TAG = nameof(PlaybackModel);

    public delegate void PlaybackStateChangedEventHandler(PlaybackStateChangedEventArgs args);
    public event PlaybackStateChangedEventHandler? PlaybackStateChanged;

    public delegate void PlayQueueChangedEventHandler();
    public event PlayQueueChangedEventHandler? PlayQueueChanged;

    private PlayQueueModel _playQueue = PlayQueueModel.CreateEmpty();
    private readonly List<PlayQueueModel.PlayQueueItem> _items = [];
    private int _cursor = 0;
    private int _randomSeed = Random.Shared.Next();
    private string? _firstId;
    private long _initialPositionMs = -1;

    public IReadOnlyList<PlayQueueModel.PlayQueueItem> Items => _items;
    public bool CanGoNext => _repeatMode == RepeatMode.All || _cursor < _items.Count - 1;
    public bool CanGoPrevious => _repeatMode == RepeatMode.All || _cursor > 0;

    private RepeatMode _repeatMode = AppSettingsModel.PlaybackDefaultRepeatMode;
    public RepeatMode RepeatMode
    {
        get => _repeatMode;
        set
        {
            if (_repeatMode != value)
            {
                _repeatMode = value;
                AppSettingsModel.PlaybackDefaultRepeatMode = value;
                DispatchPlaybackStatusChange(PlaybackStateChangeReason.Other);
            }
        }
    }

    private bool _isShuffle = AppSettingsModel.PlaybackDefaultShuffle;
    public bool IsShuffle
    {
        get => _isShuffle;
        set
        {
            if (_isShuffle != value)
            {
                _isShuffle = value;
                AppSettingsModel.PlaybackDefaultShuffle = value;

                string? currentId = CurrentItem?.Id;
                if (value)
                {
                    _randomSeed = Random.Shared.Next();
                    _firstId = currentId;
                }

                UpdateInternalStates(currentId);
            }
        }
    }

    public int Cursor
    {
        get => _cursor;
        set
        {
            if (_cursor == value || value < 0 || value >= _items.Count)
            {
                return;
            }

            _cursor = value;
            ClampCursor();
            DispatchPlaybackStatusChange(PlaybackStateChangeReason.Other);
        }
    }

    public PlayQueueModel.PlayQueueItem? CurrentItem
    {
        get
        {
            if (_cursor < 0 || _cursor >= _items.Count)
            {
                return null;
            }

            return _items[_cursor];
        }
    }

    public Builder ToBuilder()
    {
        return new Builder()
        {
            CurrentId = CurrentItem?.Id,
            FirstId = _firstId,
            RepeatMode = _repeatMode,
            IsShuffle = _isShuffle,
            RandomSeed = _randomSeed,
        };
    }

    public void LoadState(PlayQueueModel playQueue, string? serializedPlayback, long initialPositionMs)
    {
        _playQueue = playQueue;
        _initialPositionMs = initialPositionMs;

        if (!string.IsNullOrEmpty(serializedPlayback) && FromSerializedString(serializedPlayback))
        {
            return;
        }

        string? firstId = playQueue.Items.Count > 0 ? playQueue.Items[0].Id : null;
        _firstId = firstId;
        UpdateInternalStates(firstId);
    }

    public void Refresh()
    {
        DispatchPlaybackStatusChange(PlaybackStateChangeReason.Refresh);
    }

    public void Next()
    {
        if (_cursor >= Items.Count - 1)
        {
            if (_repeatMode == RepeatMode.All)
            {
                _cursor = 0;
            }
            else
            {
                return;
            }
        }
        else
        {
            _cursor++;
        }

        ClampCursor();
        DispatchPlaybackStatusChange(PlaybackStateChangeReason.Next);
    }

    public void Previous(bool fromOverScroll)
    {
        if (_cursor <= 0)
        {
            if (_repeatMode == RepeatMode.All)
            {
                _cursor = Items.Count - 1;
            }
            else
            {
                return;
            }
        }
        else
        {
            _cursor--;
        }

        ClampCursor();
        DispatchPlaybackStatusChange(fromOverScroll ? PlaybackStateChangeReason.PreviousByOverScroll : PlaybackStateChangeReason.Previous);
    }

    public string ToSerializedString()
    {
        PlaybackJsonModel model = new()
        {
            CurrentId = CurrentItem?.Id,
            RepeatMode = _repeatMode,
            IsShuffle = _isShuffle,
            RandomSeed = _randomSeed,
            FirstId = _firstId,
        };
        return JsonSerializer.Serialize(model);
    }

    private void UpdateInternalStates(string? currentId)
    {
        PopulateItems();
        SetCursorById(currentId);
        DispatchPlayQueueChange();
        DispatchPlaybackStatusChange(PlaybackStateChangeReason.Other);
    }

    private bool FromSerializedString(string serialized)
    {
        PlaybackJsonModel? model;
        try
        {
            model = JsonSerializer.Deserialize<PlaybackJsonModel>(serialized);
        }
        catch (JsonException ex)
        {
            Logger.E(TAG, ex);
            return false;
        }

        if (model is null)
        {
            return false;
        }

        _repeatMode = model.RepeatMode ?? AppSettingsModel.PlaybackDefaultRepeatMode;
        _isShuffle = model.IsShuffle ?? AppSettingsModel.PlaybackDefaultShuffle;
        _randomSeed = model.RandomSeed ?? Random.Shared.Next();
        _firstId = model.FirstId ?? model.CurrentId;
        UpdateInternalStates(model.CurrentId);
        return true;
    }

    private void PopulateItems()
    {
        _items.Clear();
        _items.AddRange(_playQueue.Items);

        if (_isShuffle)
        {
            List<PlayQueueModel.PlayQueueItem> newOrder = [.. _items.OrderBy(x =>
            {
                Span<byte> buffer = stackalloc byte[sizeof(int) + sizeof(long)];
                BinaryPrimitives.WriteInt32LittleEndian(buffer, _randomSeed);
                BinaryPrimitives.WriteInt64LittleEndian(buffer[sizeof(int)..], x.Media.Id);
                return HashUtils.GetXxHash64Int(buffer);
            })];
            for (int i = 0; i < _items.Count; i++)
            {
                _items[i] = newOrder[i];
            }

            if (!string.IsNullOrEmpty(_firstId))
            {
                int index = _items.FindIndex(x => x.Id == _firstId);
                if (index >= 0)
                {
                    PlayQueueModel.PlayQueueItem firstItem = _items[index];
                    _items.RemoveAt(index);
                    _items.Insert(0, firstItem);
                }
            }
        }
    }

    private void SetCursorById(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            _cursor = 0;
        }
        else
        {
            _cursor = Math.Max(0, _items.FindIndex(x => x.Id == id));
        }

        ClampCursor();
    }

    private void ClampCursor()
    {
        _cursor = Math.Max(Math.Min(_cursor, _items.Count - 1), 0);
    }

    private void DispatchPlaybackStatusChange(PlaybackStateChangeReason reason)
    {
        long initialPositionMs = _initialPositionMs;
        _initialPositionMs = -1;

        PlaybackStateChanged?.Invoke(new()
        {
            Reason = reason,
            InitialPositionMs = initialPositionMs,
        });
    }

    private void DispatchPlayQueueChange()
    {
        PlayQueueChanged?.Invoke();
    }

    private class PlaybackJsonModel
    {
        [JsonPropertyName("CurrentId")]
        public string? CurrentId { get; set; }

        [JsonPropertyName("FirstId")]
        public string? FirstId { get; set; }

        [JsonPropertyName("RepeatMode")]
        public RepeatMode? RepeatMode { get; set; }

        [JsonPropertyName("IsShuffle")]
        public bool? IsShuffle { get; set; }

        [JsonPropertyName("RandomSeed")]
        public int? RandomSeed { get; set; }
    }

    public class Builder
    {
        public string? CurrentId { get; set; }
        public string? FirstId { get; set; }
        public RepeatMode? RepeatMode { get; set; }
        public bool? IsShuffle { get; set; }
        public int? RandomSeed { get; set; }

        public string ToSerializedString()
        {
            PlaybackJsonModel model = new()
            {
                CurrentId = CurrentId,
                RepeatMode = RepeatMode,
                IsShuffle = IsShuffle,
                RandomSeed = RandomSeed,
                FirstId = FirstId,
            };
            return JsonSerializer.Serialize(model);
        }
    }
}
