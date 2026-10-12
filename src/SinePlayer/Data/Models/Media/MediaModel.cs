// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using SinePlayer.Common.ErrorHandling;
using SinePlayer.Common.Localization;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Storage;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Lifecycle.Utils;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Database.SqlHelpers;
using SinePlayer.Data.Database;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.TagInfo;
using SinePlayer.Data.Tables;
using SinePlayer.SDK.Models;

namespace SinePlayer.Data.Models.Media;

internal sealed partial class MediaModel : IEquatable<MediaModel>, SDK.Plugins.Media.IMediaModel
{
    private const string TAG = nameof(MediaModel);

    //
    // Events
    //

    private static readonly MutableLiveData<bool> _isScanningLibraryLiveData = new(false);
    public static LiveData<bool> IsScanningLibraryLiveData => _isScanningLibraryLiveData;

    static MediaModel()
    {
        GlobalEvent.Instance.CollectionUpdated.Observe(AlwaysActiveLifecycleOwner.Instance, ids => _ = UpdateCollections(triggerCollectionIds: ids));
        GlobalEvent.Instance.MediaUpdated.Observe(AlwaysActiveLifecycleOwner.Instance, ids => _ = UpdateCollections(triggerMediaIds: ids));
    }

    //
    // Creators
    //

    public static MediaModel CreateCollection()
    {
        return new MediaModel(CollectionMediaHandle.FromExternal());
    }

    public static async Task<MediaModel?> FromId(long id)
    {
        if (TryGetExisting(id, out MediaModel? model))
        {
            return model;
        }

        MediaHandle? mediaHandle = await MediaHandle.FromId(id);
        if (mediaHandle is not null)
        {
            return ReplaceWithExisting(mediaHandle);
        }

        return null;
    }

    public static async Task<MediaModel?> FromLocation(string location)
    {
        MediaHandle? mediaHandle = await MediaHandle.FromLocation(location);
        if (mediaHandle is not null)
        {
            return ReplaceWithExisting(mediaHandle);
        }

        if (TryGetExisting(location, out MediaModel? model))
        {
            return model;
        }

        return null;
    }

    public static async Task<MediaModel?> FromFile(string path)
    {
        string extension = Path.GetExtension(path).ToLower();
        if (!AppInfoProvider.IsSupportedVideoExtension(extension) && !AppInfoProvider.IsSupportedAudioExtension(extension))
        {
            Logger.E(TAG, $"Unsupported media file extension: {extension}");
            return null;
        }

        MediaHandle? media = await MediaHandle.FromLocation(path);
        media ??= FileMediaHandle.FromExternal(path);
        return ReplaceWithExisting(media);
    }

    public static Task<MediaModel?> FromExternalLocation(string location)
    {
        if (File.Exists(location))
        {
            string extension = Path.GetExtension(location);
            if (!AppInfoProvider.IsSupportedExternalFileExtension(extension))
            {
                Logger.E(TAG, $"Unsupported external file extension: {extension}");
                return Task.FromResult<MediaModel?>(null);
            }

            MediaHandle media = FileMediaHandle.FromExternal(location);
            return Task.FromResult<MediaModel?>(ReplaceWithExisting(media));
        }

        Logger.E(TAG, $"Invalid location: {location}");
        return Task.FromResult<MediaModel?>(null);
    }

    public static async Task<List<MediaModel>> BatchFromId(IEnumerable<long> ids)
    {
        HashSet<long> idsUnique = [.. ids.Where(x => x >= 0)];
        List<MediaModel> results = [];
        List<long> requestingIds = [];

        foreach (long id in idsUnique)
        {
            if (TryGetExisting(id, out MediaModel? model))
            {
                results.Add(model);
            }
            else
            {
                requestingIds.Add(id);
            }
        }

        if (requestingIds.Count > 0)
        {
            List<MediaHandle> requestResults = await MediaHandle.BatchFromId(requestingIds);
            foreach (MediaHandle result in requestResults)
            {
                results.Add(ReplaceWithExisting(result));
            }
        }

        return results;
    }

    //
    // Collections Update
    //

    private static readonly Lock _updateCollectionLock = new();
    private static readonly HashSet<long> _updateCollectionPendingMediaIds = [];
    private static readonly HashSet<long> _updateCollectionPendingCollectionIds = [];
    private static bool _isUpdatingCollections = false;

    public static Task UpdateCollections(IEnumerable<long>? triggerMediaIds = null, IEnumerable<long>? triggerCollectionIds = null)
    {
        void AddPendingIds(HashSet<long> target, IEnumerable<long>? ids)
        {
            if (ids is null)
            {
                return;
            }

            foreach (long id in ids)
            {
                if (id >= 0)
                {
                    target.Add(id);
                }
            }
        }

        bool shouldStartWorker = false;

        lock (_updateCollectionLock)
        {
            AddPendingIds(_updateCollectionPendingMediaIds, triggerMediaIds);
            AddPendingIds(_updateCollectionPendingCollectionIds, triggerCollectionIds);

            if (!_isUpdatingCollections
                && (_updateCollectionPendingMediaIds.Count > 0 || _updateCollectionPendingCollectionIds.Count > 0))
            {
                _isUpdatingCollections = true;
                shouldStartWorker = true;
            }
        }

        if (!shouldStartWorker)
        {
            return Task.CompletedTask;
        }

        return TaskDispatcher.DefaultThreadPool.SubmitAsync(UpdateCollectionsWorker);
    }

    private static async Task UpdateCollectionsWorker()
    {
        while (true)
        {
            List<long> mediaIds;
            List<long> collectionIds;

            lock (_updateCollectionLock)
            {
                if (_updateCollectionPendingMediaIds.Count == 0 && _updateCollectionPendingCollectionIds.Count == 0)
                {
                    _isUpdatingCollections = false;
                    break;
                }

                mediaIds = [.. _updateCollectionPendingMediaIds];
                collectionIds = [.. _updateCollectionPendingCollectionIds];
                _updateCollectionPendingMediaIds.Clear();
                _updateCollectionPendingCollectionIds.Clear();
            }

            await UpdateCollectionsInternal(mediaIds, collectionIds);
        }
    }

    private static async Task UpdateCollectionsInternal(List<long> triggerMediaIds, List<long> triggerCollectionIds)
    {
        Dictionary<long, List<long>> collectionMediaIds = await MediaHandle.Enqueue(() =>
        {
            HashSet<long> collectionIds = [.. triggerCollectionIds];
            foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(triggerMediaIds))
            {
                SelectCommand command = SelectCommand.Create(MediaCollectionTable.Instance)
                    .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaCollectionTable.ColumnMediaId), idChunk));
                IReaderToken<long> collectionIdToken = command.PutQueryInt64(MediaCollectionTable.ColumnCollectionId);
                using SelectCommand.IReader reader = command.Execute();
                while (reader.Read())
                {
                    collectionIds.Add(collectionIdToken.GetValue());
                }
            }

            Dictionary<long, List<long>> results = [];
            foreach (long collectionId in collectionIds)
            {
                results[collectionId] = [];
            }

            foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(collectionIds))
            {
                SelectCommand command = SelectCommand.Create(MediaCollectionTable.Instance)
                    .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaCollectionTable.ColumnCollectionId), idChunk));
                IReaderToken<long> collectionIdToken = command.PutQueryInt64(MediaCollectionTable.ColumnCollectionId);
                IReaderToken<long> mediaIdToken = command.PutQueryInt64(MediaCollectionTable.ColumnMediaId);
                using SelectCommand.IReader reader = command.Execute();
                while (reader.Read())
                {
                    long collectionId = collectionIdToken.GetValue();
                    long mediaId = mediaIdToken.GetValue();
                    if (!results.TryGetValue(collectionId, out List<long>? linkedMediaIds))
                    {
                        linkedMediaIds = [];
                        results[collectionId] = linkedMediaIds;
                    }

                    linkedMediaIds.Add(mediaId);
                }
            }

            return results;
        });

        if (collectionMediaIds.Count == 0)
        {
            return;
        }

        Dictionary<long, MediaModel> mediaMap = [];
        {
            HashSet<long> linkedMediaIds = [.. collectionMediaIds.Values.SelectMany(x => x)];
            List<MediaModel> linkedMedia = await BatchFromId(linkedMediaIds);
            foreach (MediaModel media in linkedMedia)
            {
                if (media.IsCollection)
                {
                    continue;
                }

                mediaMap[media.Id] = media;
            }
        }

        List<MediaModel> collections = await BatchFromId(collectionMediaIds.Keys);

        foreach (MediaModel collection in collections)
        {
            if (!collection.IsCollection)
            {
                continue;
            }

            List<MediaModel> linkedMediaOfCollection = [];
            foreach (long mediaId in collectionMediaIds[collection.Id])
            {
                if (mediaMap.TryGetValue(mediaId, out MediaModel? media))
                {
                    linkedMediaOfCollection.Add(media);
                }
            }

            await UpdateCollection(collection, linkedMediaOfCollection);
        }
    }

    private static async Task UpdateCollection(MediaModel collection, List<MediaModel> mediaItems)
    {
        int mediaCount = mediaItems.Count;
        bool hasReadingMedia = false;
        long totalDuration = 0;
        int totalProgress = 0;

        foreach (MediaModel media in mediaItems)
        {
            if (media.CompletionStatus == CompletionStatusEnum.Ongoing)
            {
                hasReadingMedia = true;
            }

            long mediaDurationMs = media.DurationMs;
            if (mediaDurationMs >= 0)
            {
                totalDuration += mediaDurationMs;
            }

            int mediaProgress = media.Progress;
            if (mediaProgress >= 0)
            {
                totalProgress += Math.Clamp(mediaProgress, 0, 100);
            }
        }

        await collection.SetDuration(totalDuration);

        int progress = mediaCount > 0 ? totalProgress / mediaCount : 100;
        await collection.SetProgress(progress, 0);

        if (hasReadingMedia && CompletionStatusService.CanTransitToOngoingAutomatically(collection.CompletionStatus))
        {
            await collection.SetCompletionStatus(CompletionStatusEnum.Ongoing);
        }
    }

    //
    // Library Operations
    //

    private static readonly Lock _alterLibraryLock = new();
    private static readonly Queue<QueuedTask> _pendingAlterLibraryTasks = [];
    private static bool _isAlteringLibrary = false;
    private static bool _isLibraryScanPending = false;

    public static void RescanLibrary(string reason)
    {
        Logger.I(TAG, $"UpdateAllMedia (reason={reason})");

        lock (_alterLibraryLock)
        {
            if (_isLibraryScanPending)
            {
                return;
            }

            _isLibraryScanPending = true;
        }

        AlterLibrary(() => Task.CompletedTask); // Start a worker if not
    }

    public static async Task RemoveMedia(IEnumerable<MediaModel> mediaItems)
    {
        await AlterLibrary(() => RemoveMediaInternal(mediaItems));
    }

    public static Task<List<string>> GetAllTagCategories()
    {
        return MediaHandle.Enqueue<List<string>>(() =>
        {
            HashSet<string> tags = [];
            var command = SelectCommand.Create(MediaTagCategoryTable.Instance);
            IReaderToken<string> nameToken = command.PutQueryString(MediaTagCategoryTable.ColumnName);
            command.Distinct();
            using SelectCommand.IReader reader = command.Execute();
            while (reader.Read())
            {
                string name = nameToken.GetValue();
                tags.Add(name);
            }
            return [.. tags];
        });
    }

    private static Task AlterLibrary(Func<Task> func)
    {
        QueuedTask item = new(func);
        bool shouldStartWorker;

        lock (_alterLibraryLock)
        {
            _pendingAlterLibraryTasks.Enqueue(item);
            shouldStartWorker = !_isAlteringLibrary;
            _isAlteringLibrary = true;
        }

        if (shouldStartWorker)
        {
            TaskDispatcher.DefaultThreadPool.SubmitAsync(AlterLibraryWorker);
        }

        return item.Task;
    }

    private static async Task AlterLibraryWorker()
    {
        while (true)
        {
            QueuedTask? next;

            lock (_alterLibraryLock)
            {
                if (_pendingAlterLibraryTasks.Count > 0)
                {
                    next = _pendingAlterLibraryTasks.Dequeue();
                }
                else if (_isLibraryScanPending)
                {
                    _isLibraryScanPending = false;
                    next = new(async () =>
                    {
                        _isScanningLibraryLiveData.Emit(true);
                        try
                        {
                            await RescanLibraryInternal();
                        }
                        finally
                        {
                            _isScanningLibraryLiveData.Emit(false);
                        }
                    });
                }
                else
                {
                    _isAlteringLibrary = false;
                    return;
                }
            }

            await next.ExecuteAsync();
        }
    }

    private static async Task RescanLibraryInternal()
    {
        AppSettingsModel.ExternalModel appSettings = AppSettingsModel.GetModel();
        HashSet<long> changedMediaIds = [];

        // Get all locations from database
        HashSet<string> oldLocations = [];
        await MediaHandle.Enqueue(() =>
        {
            SelectCommand command = SelectCommand.Create(MediaTable.Instance)
                .AppendCondition(MediaHandle.CreateMediaOnlyCondition());
            IReaderToken<string> locationToken = command.PutQueryString(MediaTable.ColumnLocation);
            using SelectCommand.IReader reader = command.Execute();
            while (reader.Read())
            {
                oldLocations.Add(locationToken.GetValue());
            }
        });

        // Scan mediaItems
        Dictionary<string, MediaType> pendingLocations = [];
        HashSet<string> newLocations = [];
        HashSet<string> noAccessLocations = [];

        async Task FlushPendingLocations()
        {
            List<UpdateItemInfo> updateQueue = [];
            foreach (KeyValuePair<string, MediaType> pair in pendingLocations)
            {
                string location = pair.Key;
                MediaType type = pair.Value;

                if (!newLocations.Add(location))
                {
                    continue;
                }

                if (oldLocations.Contains(location))
                {
                    continue;
                }

                if (MediaImportExclusionModel.Instance.Contains(location))
                {
                    continue;
                }

                updateQueue.Add(new UpdateItemInfo
                {
                    Location = location,
                    ItemType = type,
                });
            }

            pendingLocations.Clear();

            if (updateQueue.Count > 0)
            {
                await TransactionBlock(() =>
                {
                    foreach (UpdateItemInfo info in updateQueue)
                    {
                        long mediaId = MediaHandle.InsertNoLock(info.ItemType, info.Location);
                        if (mediaId >= 0)
                        {
                            changedMediaIds.Add(mediaId);
                        }
                    }
                });
            }
        }

        var watch = new Stopwatch();
        watch.Start();

        foreach (string folderPath in appSettings.MediaFolders)
        {
            if (!Directory.Exists(folderPath))
            {
                Logger.I(TAG, $"Folder not exists, skipped: {folderPath}");
                continue;
            }

            foreach (MediaScanner.ItemInfo itemInfo in MediaScanner.Search(folderPath))
            {
                if (_isLibraryScanPending)
                {
                    return; // Fast exit
                }

                switch (itemInfo.Type)
                {
                    case MediaScanner.ItemType.Folder:
                        continue;
                    case MediaScanner.ItemType.File:
                        break;
                    case MediaScanner.ItemType.NoAccessLocation:
                        noAccessLocations.Add(itemInfo.Path);
                        continue;
                    default:
                        Logger.F(TAG, $"Unknown item type '{itemInfo.Type}' for path '{itemInfo.Path}'");
                        continue;
                }

                string filename = StringUtils.ItemNameFromPath(itemInfo.Path);
                string extension = StringUtils.ExtensionFromFilename(filename).ToLowerInvariant();
                if (AppInfoProvider.IsSupportedVideoExtension(extension))
                {
                    pendingLocations[itemInfo.Path] = MediaType.Video;
                }
                else if (AppInfoProvider.IsSupportedAudioExtension(extension))
                {
                    pendingLocations[itemInfo.Path] = MediaType.Audio;
                }

                if (watch.LapSpan().TotalSeconds > 2)
                {
                    await FlushPendingLocations();

                    if (changedMediaIds.Count > 0)
                    {
                        DispatchUpdateEvent(changedMediaIds);
                        changedMediaIds.Clear();
                    }

                    watch.Lap();
                }
            }
        }

        await FlushPendingLocations();

        // Remove unreachable media items
        if (appSettings.RemoveUnreachableMedia)
        {
            List<string> locationRemoved = [.. oldLocations.Except(newLocations)];

            for (int i = locationRemoved.Count - 1; i >= 0; i--)
            {
                string location = locationRemoved[i];
                foreach (string noAccessLocation in noAccessLocations)
                {
                    if (StringUtils.FolderContain(noAccessLocation, location))
                    {
                        locationRemoved.RemoveAt(i);
                        break;
                    }
                }
            }

            if (locationRemoved.Count > 0)
            {
                bool proceed = true;
                if (appSettings.PromptBeforeRemovingMedia)
                {
                    string promptContent = StringResourceProvider.Instance.RemoveItemsConfirmation
                        .Replace("$count", locationRemoved.Count.ToString())
                        .Replace("$items", string.Join('\n', locationRemoved));
                    DialogOptions options = new DialogOptions.Builder()
                        .SetTitle(StringResourceProvider.Instance.Warning)
                        .SetContent(promptContent)
                        .SetPrimaryButtonText(StringResourceProvider.Instance.Remove)
                        .SetCloseButtonText(StringResourceProvider.Instance.Cancel)
                        .Build();
                    DialogResult result = await DialogUtils.EnqueueDialogAsync(options);
                    proceed = result == DialogResult.Primary;
                }

                if (proceed)
                {
                    List<long> removingMediaIds = [];
                    await MediaHandle.Enqueue(() =>
                    {
                        foreach (string location in locationRemoved)
                        {
                            List<long> mediaIds = [];
                            SelectCommand command = SelectCommand.Create(MediaTable.Instance)
                                .AppendCondition(MediaTable.ColumnLocation, location)
                                .AppendCondition(MediaHandle.CreateMediaOnlyCondition());
                            IReaderToken<long> idToken = command.PutQueryInt64(MediaTable.ColumnId);
                            using SelectCommand.IReader reader = command.Execute();
                            while (reader.Read())
                            {
                                mediaIds.Add(idToken.GetValue());
                            }

                            if (mediaIds.Count == 0)
                            {
                                Logger.F(TAG, $"Removing: No rows found for location '{location}'");
                                continue;
                            }

                            if (mediaIds.Count != 1)
                            {
                                Logger.F(TAG, $"Removing: Found {mediaIds.Count} rows for location '{location}'");
                            }

                            Logger.I(TAG, $"Removing: {location}");
                            removingMediaIds.AddRange(mediaIds);
                        }
                    });

                    List<MediaModel> removingMedia = await BatchFromId(removingMediaIds);
                    await RemoveMediaInternal(removingMedia);
                }
            }
        }

        if (changedMediaIds.Count > 0)
        {
            DispatchUpdateEvent(changedMediaIds);
        }
    }

    private static async Task RemoveMediaInternal(IEnumerable<MediaModel> mediaItems)
    {
        List<MediaModel> removingMedia = [.. mediaItems.Where(x => !x.IsExternal).Distinct()];

        List<long> ids = [.. removingMedia.Select(x => x.Id)];
        if (ids.Count == 0)
        {
            return;
        }

        await MediaHandle.Enqueue(() =>
        {
            foreach (MediaModel media in removingMedia)
            {
                media._internalModel.Id = -1;
            }
        });

        HashSet<long> affectedCollectionIds = await MediaHandle.Enqueue(() =>
        {
            HashSet<long> collectionIds = [];
            foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(ids))
            {
                SelectCommand command = SelectCommand.Create(MediaCollectionTable.Instance)
                    .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaCollectionTable.ColumnMediaId), idChunk));
                IReaderToken<long> collectionIdToken = command.PutQueryInt64(MediaCollectionTable.ColumnCollectionId);
                using SelectCommand.IReader reader = command.Execute();
                while (reader.Read())
                {
                    collectionIds.Add(collectionIdToken.GetValue());
                }
            }

            return collectionIds;
        });

        List<string> resourceIds = [];
        foreach (MediaModel media in removingMedia)
        {
            string? resourceId = media.GetExt(MediaExt.RESOURCE_UUID);
            if (!string.IsNullOrEmpty(resourceId))
            {
                resourceIds.Add(resourceId);
            }
        }

        await MediaHandle.Enqueue(() =>
        {
            SqliteDB.MediaDatabase.WithTransaction(() =>
            {
                foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(ids))
                {
                    DeleteCommand.Create(MediaTagTable.Instance)
                        .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaTagTable.ColumnMediaId), idChunk))
                        .Execute();
                    DeleteCommand.Create(MediaTagCategoryTable.Instance)
                        .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaTagCategoryTable.ColumnMediaId), idChunk))
                        .Execute();
                    DeleteCommand.Create(MediaCollectionTable.Instance)
                        .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaCollectionTable.ColumnMediaId), idChunk))
                        .Execute();
                    DeleteCommand.Create(MediaCollectionTable.Instance)
                        .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaCollectionTable.ColumnCollectionId), idChunk))
                        .Execute();
                    DeleteCommand.Create(MediaTable.Instance)
                        .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaTable.ColumnId), idChunk))
                        .Execute();
                }
            });
        });

        await SqliteDB.MiscDatabaseDispatcher.Submit(() =>
        {
            foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(ids))
            {
                DeleteCommand.Create(MediaHistoryTable.Instance)
                    .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaHistoryTable.ColumnMediaId), idChunk))
                    .Execute();
            }
        });

        FavoriteModel.Instance.BatchRemoveWithId([.. ids]);

        List<Task> resourceTasks = [];
        foreach (string resourceId in resourceIds)
        {
            resourceTasks.Add(ResourceManager.Release(resourceId));
        }

        await Task.WhenAll(resourceTasks);

        foreach (MediaModel media in removingMedia)
        {
            _locationPool.GetOrAdd(media.Location, media);
        }

        foreach (long id in ids)
        {
            _idPool.TryRemove(id, out _);
        }

        if (affectedCollectionIds.Count > 0)
        {
            await UpdateCollections(triggerCollectionIds: affectedCollectionIds);
        }

        DispatchUpdateEvent(ids);
    }

    //
    // Pool
    //

    private static readonly ConcurrentWeakPool<long, MediaModel> _idPool = new();
    private static readonly ConcurrentWeakPool<string, MediaModel> _locationPool = new();

    private static bool TryGetExisting(long id, [MaybeNullWhen(false)] out MediaModel model)
    {
        return _idPool.TryGetValue(id, out model);
    }

    private static bool TryGetExisting(string location, [MaybeNullWhen(false)] out MediaModel model)
    {
        return _locationPool.TryGetValue(location, out model);
    }

    private static MediaModel ReplaceWithExisting(MediaHandle mediaHandle)
    {
        var model = new MediaModel(mediaHandle);

        if (!model.IsExternal)
        {
            return _idPool.GetOrAdd(model.Id, model);
        }

        return _locationPool.GetOrAdd(model.Location, model);
    }

    //
    // Static Helpers
    //

    private static Task TransactionBlock(Action action)
    {
        return MediaHandle.Enqueue(() =>
        {
            SqliteDB.MediaDatabase.WithTransaction(action);
        });
    }

    private static void DispatchUpdateEvent(IEnumerable<long> mediaIds, int delay = 0)
    {
        List<long> filtered = [.. mediaIds.Where(x => x >= 0)];
        if (filtered.Count == 0)
        {
            return;
        }

        GlobalEvent.Instance.MediaUpdated.EmitDelayed(filtered, delay);
    }

    //
    // Constructors
    //

    private static int _highestObjectId = 0;
    private readonly int _objectId;
    private readonly MediaHandle _internalModel;

    private MediaModel(MediaHandle mediaData)
    {
        _objectId = Interlocked.Increment(ref _highestObjectId);
        _internalModel = mediaData;
    }

    //
    // Equality
    //

    public bool Equals(MediaModel? other)
    {
        if (other is null)
        {
            return false;
        }

        return other._objectId == _objectId;
    }

    public override bool Equals(object? obj)
    {
        if (obj is not MediaModel media)
        {
            return false;
        }

        return Equals(media);
    }

    public override int GetHashCode()
    {
        return _objectId.GetHashCode();
    }

    public static bool operator ==(MediaModel? left, MediaModel? right)
        => EqualityComparer<MediaModel>.Default.Equals(left, right);

    public static bool operator !=(MediaModel? left, MediaModel? right)
        => !(left == right);

    //
    // Getters/Setters
    //

    public long Id => _internalModel.Id;
    public MediaType Type => _internalModel.Type;
    public bool IsHidden => _internalModel.IsHidden;
    public bool IsEditable => _internalModel.IsEditable;
    public bool IsExternal => _internalModel.IsExternal;
    public bool IsCollection => _internalModel.IsCollection;
    public long LastPositionMs => _internalModel.LastPositionMs;
    public string Location => _internalModel.Location;
    public int Progress => _internalModel.Progress;
    public DateTimeOffset LastVisit => _internalModel.LastVisit;
    public int Rating => _internalModel.Rating;
    public IReadOnlyDictionary<string, MediaTagCategory> Tags => _internalModel.Tags;
    public string Title1 => _internalModel.Title1;
    public string Title2 => _internalModel.Title2;
    public string Description => _internalModel.Description;
    public CompletionStatusEnum CompletionStatus => _internalModel.CompletionStatus;
    public long DurationMs => _internalModel.DurationMs;
    public IReadOnlyList<string> FolderViewPath => _internalModel.GetFolderViewPath();
    public Dictionary<string, HashSet<string>> TagsCopy => _internalModel.Tags.ToDictionary(p => p.Key, p => p.Value.Tags.ToHashSet());

    public string Title
    {
        get
        {
            if (Title1.Length == 0)
            {
                if (Title2.Length == 0)
                {
                    return StringResourceProvider.Instance.Untitled;
                }
                else
                {
                    return Title2;
                }
            }
            else if (Title2.Length == 0)
            {
                return Title1;
            }
            else
            {
                return Title1 + " - " + Title2;
            }
        }
    }

    public string? GetExt(string key)
    {
        return _internalModel.GetExt(key);
    }

    public async Task Save()
    {
        bool wasExternal = IsExternal;
        await _internalModel.Save();

        if (wasExternal)
        {
            _idPool.Set(Id, this);
        }

        DispatchUpdateEvent([Id]);
    }

    public void SetExt(string key, string? value)
    {
        _internalModel.SetExt(key, value);
        DispatchUpdateEvent([Id]);
    }

    public async Task FlushExt()
    {
        await _internalModel.FlushExt();
    }

    public async Task SetTitle1(string title)
    {
        await _internalModel.SetTitle1(title);
        DispatchUpdateEvent([Id]);
    }

    public async Task SetTitle2(string title)
    {
        await _internalModel.SetTitle2(title);
        DispatchUpdateEvent([Id]);
    }

    public async Task SetDescription(string description)
    {
        await _internalModel.SetDescription(description);
        DispatchUpdateEvent([Id]);
    }

    public async Task SetRating(int rating)
    {
        if (Rating == rating)
        {
            return;
        }

        await _internalModel.SetRating(rating);
        DispatchUpdateEvent([Id]);
    }

    public async Task SetTags<T>(IEnumerable<KeyValuePair<string, T>> tags) where T : IEnumerable<string>
    {
        await _internalModel.SetTags(tags);
        DispatchUpdateEvent([Id]);
    }

    public async Task SetCompletionStatus(CompletionStatusEnum status)
    {
        bool updated = false;

        switch (status)
        {
            case CompletionStatusEnum.NotStarted:
                updated = true;
                await _internalModel.SetProgress(-1, 0);
                break;
            case CompletionStatusEnum.Ongoing:
                updated = true;
                _internalModel.SetAsVisited();
                break;
            default:
                break;
        }

        if (CompletionStatus != status)
        {
            updated = true;
            await _internalModel.SaveCompletionStatus(status);
        }

        if (updated)
        {
            DispatchUpdateEvent([Id]);
        }
    }

    public async Task SetAsVisited()
    {
        _internalModel.SetAsVisited();

        if (CompletionStatusService.CanTransitToOngoingAutomatically(CompletionStatus))
        {
            await _internalModel.SaveCompletionStatus(CompletionStatusEnum.Ongoing);
        }

        DispatchUpdateEvent([Id]);
    }

    public async Task SetProgress(int progress, long lastPositionMs)
    {
        if (Progress == progress && LastPositionMs == lastPositionMs)
        {
            return;
        }

        await _internalModel.SetProgress(progress, lastPositionMs);
        DispatchUpdateEvent([Id], delay: 5000);
    }

    public async Task SetHidden(bool isHidden)
    {
        if (IsHidden == isHidden)
        {
            return;
        }

        await _internalModel.SaveHiddenAsync(isHidden);
        DispatchUpdateEvent([Id]);
    }

    public async Task SetLocation(string location)
    {
        await _internalModel.SetLocation(location);
    }

    public async Task SetDuration(long durationMs)
    {
        if (DurationMs == durationMs)
        {
            return;
        }

        _internalModel.DurationMs = durationMs;
        await MediaHandle.Enqueue(() =>
        {
            if (IsExternal)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnDurationMs, _internalModel.GetColumnValue(MediaTable.ColumnDurationMs))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        });

        DispatchUpdateEvent([Id]);
    }

    //
    // Helpers
    //

    public Task<ErrorResult> ShowInFileExplorer()
    {
        if (IsCollection)
        {
            var err = ErrorLogger.Create(TAG);
            return Task.FromResult(err.Error("A collection has no file location."));
        }

        return ThirdPartyLauncher.ShowInFileExplorer(_internalModel.FileSystemPath);
    }

    //
    // SDK.Plugins.Media.IMediaModel Implementation
    //

    long SDK.Plugins.Media.IMediaModel.Id => Id;

    string SDK.Plugins.Media.IMediaModel.Location => Location;

    // The page-based model was removed with the media reader; the plugin SDK rework is deferred.
    int SDK.Plugins.Media.IMediaModel.PageCount => -1;

    string SDK.Plugins.Media.IMediaModel.Title1 => Title1;

    string SDK.Plugins.Media.IMediaModel.Title2 => Title2;

    string SDK.Plugins.Media.IMediaModel.Description => Description;

    int SDK.Plugins.Media.IMediaModel.Rating => Rating;

    IReadOnlyDictionary<string, SDK.Plugins.Media.IMediaTagCategory> SDK.Plugins.Media.IMediaModel.Tags => _internalModel.TagsForPlugin;

    IReadOnlyDictionary<string, string> SDK.Plugins.Media.IMediaModel.Links
    {
        get
        {
            Dictionary<string, string> links = [];

            string? linkJson = GetExt(MediaExt.LINKS);
            var linkModel = TagLinkModel.Parse(linkJson);
            if (linkModel is null)
            {
                return links;
            }

            foreach (TagLinkModel.LinkModel item in linkModel.Links)
            {
                links[item.Name] = item.Link;
            }

            return links;
        }
    }

    bool SDK.Plugins.Media.IMediaModel.IsHidden => IsHidden;

    SDK.Plugins.Media.CompletionStatusEnum SDK.Plugins.Media.IMediaModel.CompletionStatus =>
        CompletionStatusService.HostEnumToSDKEnum(CompletionStatus);

    Task SDK.Plugins.Media.IMediaModel.SetTitle1(string title)
    {
        return SetTitle1(title);
    }

    Task SDK.Plugins.Media.IMediaModel.SetTitle2(string title)
    {
        return SetTitle2(title);
    }

    Task SDK.Plugins.Media.IMediaModel.SetDescription(string description)
    {
        return SetDescription(description);
    }

    Task SDK.Plugins.Media.IMediaModel.SetRating(int rating)
    {
        return SetRating(rating);
    }

    Task SDK.Plugins.Media.IMediaModel.SetTags<T>(IEnumerable<KeyValuePair<string, T>> tags)
    {
        return SetTags(tags);
    }

    Task SDK.Plugins.Media.IMediaModel.SetLinks(IEnumerable<KeyValuePair<string, string>> links)
    {
        List<TagLinkModel.LinkModel> linkModels = [];
        foreach (KeyValuePair<string, string> item in links)
        {
            linkModels.Add(new()
            {
                Name = item.Key,
                Link = item.Value,
            });
        }

        TagLinkModel linkModel = new()
        {
            Links = linkModels,
        };
        string json = linkModel.Serialize();
        SetExt(MediaExt.LINKS, json);
        return FlushExt();
    }

    Task SDK.Plugins.Media.IMediaModel.SetHidden(bool isHidden)
    {
        return SetHidden(isHidden);
    }

    async Task SDK.Plugins.Media.IMediaModel.SetCompletionStatus(SDK.Plugins.Media.CompletionStatusEnum status)
    {
        CompletionStatusEnum convertedStatus = CompletionStatusService.SDKEnumToHostEnum(status);
        await _internalModel.SaveCompletionStatus(convertedStatus);
        DispatchUpdateEvent([Id]);
    }

    async Task<bool> SDK.Plugins.Media.IMediaModel.MoveToLocation(string location)
    {
        string oldLocation = Location;
        bool success = await _internalModel.MoveToLocation(location);

        if (success)
        {
            if (IsExternal)
            {
                _locationPool.TryRemove(oldLocation, out _);
                _locationPool.GetOrAdd(location, this);
            }

            DispatchUpdateEvent([Id]);
        }

        return success;
    }

    //
    // Types
    //

    private struct UpdateItemInfo
    {
        public string Location;
        public MediaType ItemType;
    };

    private sealed class QueuedTask(Func<Task> func)
    {
        private readonly Func<Task> _func = func;
        private readonly TaskCompletionSource _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _source.Task;

        public async Task ExecuteAsync()
        {
            try
            {
                await _func();
                _source.SetResult();
            }
            catch (Exception ex)
            {
                _source.SetException(ex);
            }
        }
    }
}
