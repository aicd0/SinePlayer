// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;

using SinePlayer.Common.Misc;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.ServiceManagement;
using SinePlayer.Core.Common.ServiceManagement.Services;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.Downmix;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Gain;
using SinePlayer.Core.Playback.Hosting;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;

namespace SinePlayer.Common.Playback.Core;

internal sealed partial class PlayerSession : IDisposable
{
    #region Constructor

    private const string TAG = nameof(PlayerSession);

    private const int PositionIntervalMilliseconds = 250;
    private const long AlmostEndedThresholdMilliseconds = 5000;
    private const long ProgressSaveIntervalMilliseconds = 5000;
    private const int VolumeSaveDelayMilliseconds = 500;

    private bool _disposed;
    private readonly AudioDeviceNotifier _audioDeviceNotifier = AudioDeviceNotifier.Shared;
    private readonly List<PlayerView> _views = [];
    private readonly List<AudioChainEntry> _audioChains = [];

    private bool _hasSource = false;
    private PlaybackComponents _components = PlaybackComponents.Default;
    private double _volume = 1.0;
    private bool _muted;

    private bool _pendingReconcile = false;
    private ReconcileResult? _lastReconcile;

    private PlayQueueModel _playQueue = PlayQueueModel.CreateEmpty();
    private bool _loaded;
    private Task? _loadTask;
    private TrackSelectionModel? _loadedSelection;

    public PlayerSession(string id)
    {
        Id = id;
        _audioDeviceNotifier.Subscribe(AudioDevices_Changed);
        Playback.PlaybackStateChanged += Playback_PlaybackStateChanged;
        Playback.PlayQueueChanged += Playback_PlayQueueChanged;
    }

    private sealed record AudioChainEntry(IAudioChain Chain, GainAudioFilter Gain, DownmixAudioFilter Downmix);

    #endregion

    #region Public API

    public static string NewId()
    {
        return Guid.NewGuid().ToString();
    }

    public event Action<MediaEngineState, string?>? StateChanged;

    public event Action? LoadingChanged;

    public event Action<MediaModel?, OperationResult>? MediaChanged;

    public string Id { get; }

    public PlayQueueModel PlayQueue => _playQueue;

    public PlaybackModel Playback { get; } = new();

    public MediaModel? CurrentMedia => Playback.CurrentItem?.Media;

    public event Action? PositionChanged;

    public event Action? VolumeChanged;

    public MediaEngineState State => _pipeline?.State ?? MediaEngineState.None;

    public bool IsLoading => Volatile.Read(ref _loadingCount) > 0;

    public string? Error => _pipeline?.Error;

    public IReadOnlyList<MediaSource> Sources => _pipeline?.Sources ?? [];

    public IReadOnlyList<IAudioChain> AudioChains => [.. _audioChains.Select(entry => entry.Chain)];

    public TimeSpan Position => _pipeline?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration
    {
        get
        {
            TimeSpan video = _views.Count == 0
                ? TimeSpan.Zero
                : _views.Max(view => view.VideoChain?.SelectedStream?.Duration ?? TimeSpan.Zero);
            TimeSpan audio = _audioChains.Count == 0
                ? TimeSpan.Zero
                : _audioChains.Max(entry => entry.Chain.SelectedStream?.Duration ?? TimeSpan.Zero);
            TimeSpan longest = video > audio ? video : audio;
            if (longest > TimeSpan.Zero)
            {
                return longest;
            }

            IReadOnlyList<MediaSource> sources = Sources;
            return sources.Count == 0 ? TimeSpan.Zero : sources.Max(source => source.Info.Duration);
        }
    }

    public double Volume => _volume;

    public bool IsMuted => _muted;

    public double PlaybackRate => 1.0;

    public Task EnsureLoaded()
    {
        return _loadTask ??= LoadRecordAsync();

        async Task LoadRecordAsync()
        {
            var record = PlayerSessionRecord.Load(Id);
            PlayQueueModel playQueue = record?.PlayQueue is { } items
                ? await PlayQueueModel.CreateFromJsonModel(items)
                : PlayQueueModel.CreateEmpty();

            _loaded = true;
            _loadedSelection = record?.TrackSelection;
            _playQueue = playQueue;
            Playback.LoadState(playQueue, record?.Playback, -1);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Playback.PlaybackStateChanged -= Playback_PlaybackStateChanged;
        _audioDeviceNotifier.Unsubscribe(AudioDevices_Changed);

        StopPositionTimer();

        SaveProgress();
        SaveRecord();

        foreach (PlayerView view in _views)
        {
            view.DetachChains();
        }

        PlaybackPipeline? pipeline = _pipeline;
        _pipeline = null;
        if (pipeline is not null)
        {
            pipeline.StateChanged -= Pipeline_StateChanged;
            pipeline.WarningRaised -= Pipeline_WarningRaised;
            pipeline.Dispose();
        }

        foreach (AudioChainEntry entry in _audioChains)
        {
            entry.Gain.Dispose();
            entry.Downmix.Dispose();
        }

        _audioChains.Clear();

        foreach (PlayerView view in _views)
        {
            view.Dispose();
        }

        _views.Clear();
    }

    public async Task<OperationResult> Reset()
    {
        PlaybackPipeline pipeline = EnsurePipeline();

        OperationResult reset = await pipeline.Reset();
        if (!reset.IsSuccessful)
        {
            return OperationResultExt.Failure(TAG, "The player could not be reset.", reset);
        }

        _hasSource = false;
        _pendingReconcile = true;
        return OperationResult.Success();
    }

    public void SetComponents(PlaybackComponents components)
    {
        ArgumentNullException.ThrowIfNull(components);

        if (_components == components)
        {
            return;
        }

        _components = components;
        _pendingReconcile = true;

        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is not null)
        {
            components.Apply(pipeline);
        }
    }

    public async Task<OperationResult<MediaSource>> AddSource(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return OperationResultExt.Failure<MediaSource>(TAG, $"Media file not found: '{filePath}'.");
        }

        PlaybackPipeline pipeline = EnsurePipeline();

        var locator = MediaLocator.Parse(filePath);
        MediaSource? existing = pipeline.Sources.FirstOrDefault(source => string.Equals(source.Locator.Value, locator.Value, StringComparison.Ordinal));
        if (existing is not null)
        {
            return OperationResultExt.Success(existing);
        }

        OperationResult<MediaSource> added = await pipeline.AddSource(locator);
        if (!added.IsSuccessful)
        {
            return OperationResultExt.Failure<MediaSource>(
                TAG,
                $"Unable to open media file '{filePath}': {added.Message}",
                added.Exception,
                added.IsFatal);
        }

        if (!_hasSource)
        {
            _hasSource = true;
            StartPositionTimer();
        }

        _pendingReconcile = true;
        return added;
    }

    public async Task<PlayerView> CreateView()
    {
        PlayerView view = new(this);
        _views.Add(view);
        SaveRecord();

        if (_openedMedia is not null)
        {
            using IDisposable loading = BeginLoading();
            await ApplyViewTracksAsync(view);
            await Reconcile();
        }

        return view;
    }

    public async Task RemoveView(PlayerView view)
    {
        if (!_views.Remove(view))
        {
            return;
        }

        SaveRecord();

        if (_pipeline is not { } pipeline)
        {
            view.Dispose();
            return;
        }

        view.DetachSurfaces();

        foreach (IPlaybackChain chain in ChainsOf(view))
        {
            await pipeline.RemoveChain(chain);
            _pendingReconcile = true;
        }

        await Reconcile();
        view.Dispose();
    }

    public async Task<OperationResult> SelectVideoStream(PlayerView view, MediaStreamInfo? stream)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return OperationResultExt.Failure(TAG, "There is no playback to change a track of.");
        }

        IVideoChain chain = await EnsureVideoChain(pipeline, view);
        OperationResult selected = await chain.SelectStream(stream);
        if (!selected.IsSuccessful)
        {
            string wanted = stream is null ? "no track" : $"track #{stream.Index}";
            return OperationResultExt.Failure(TAG, $"The picture could not be changed to {wanted}. {selected.Message}");
        }

        _pendingReconcile = true;
        SaveRecord();
        return OperationResult.Success();
    }

    public async Task<OperationResult> AddAudioStream(MediaStreamInfo stream)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return OperationResultExt.Failure(TAG, "There is nothing open to change a track of.");
        }

        if (_audioChains.Any(entry => entry.Chain.SelectedStream?.Equals(stream) == true))
        {
            return OperationResult.Success();
        }

        IAudioChain? chain = _audioChains.FirstOrDefault(entry => entry.Chain.SelectedStream is null)?.Chain;
        chain ??= await CreateAudioChain(pipeline);

        OperationResult selected = await chain.SelectStream(stream);
        if (!selected.IsSuccessful)
        {
            return OperationResultExt.Failure(TAG, $"The sound of track #{stream.Index} could not be added. {selected.Message}");
        }

        _pendingReconcile = true;
        SaveRecord();
        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveAudioStream(MediaStreamInfo stream)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return OperationResultExt.Failure(TAG, "There is nothing open to change a track of.");
        }

        AudioChainEntry? entry = _audioChains.FirstOrDefault(candidate => candidate.Chain.SelectedStream?.Equals(stream) == true);
        if (entry is null)
        {
            return OperationResult.Success();
        }

        OperationResult cleared = await entry.Chain.SelectStream(null);
        if (!cleared.IsSuccessful)
        {
            return OperationResultExt.Failure(TAG, $"The sound of track #{stream.Index} could not be taken away. {cleared.Message}");
        }

        _pendingReconcile = true;
        SaveRecord();
        return OperationResult.Success();
    }

    public async Task<OperationResult> AddSubtitleStream(PlayerView view, MediaStreamInfo stream)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return OperationResultExt.Failure(TAG, "There is no playback to change a track of.");
        }

        if (view.SubtitleChains.Any(chain => chain.SelectedStream?.Equals(stream) == true))
        {
            return OperationResult.Success();
        }

        ISubtitleChain? chain = view.SubtitleChains.FirstOrDefault(candidate => candidate.SelectedStream is null);
        chain ??= await CreateSubtitleChain(pipeline, view);

        OperationResult selected = await chain.SelectStream(stream);
        if (!selected.IsSuccessful)
        {
            return OperationResultExt.Failure(TAG, $"The subtitles of track #{stream.Index} could not be added. {selected.Message}");
        }

        _pendingReconcile = true;
        SaveRecord();
        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveSubtitleStream(PlayerView view, MediaStreamInfo stream)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return OperationResultExt.Failure(TAG, "There is no playback to change a track of.");
        }

        ISubtitleChain? chain = view.SubtitleChains.FirstOrDefault(candidate => candidate.SelectedStream?.Equals(stream) == true);
        if (chain is null)
        {
            return OperationResult.Success();
        }

        OperationResult cleared = await chain.SelectStream(null);
        if (!cleared.IsSuccessful)
        {
            return OperationResultExt.Failure(TAG, $"The subtitles of track #{stream.Index} could not be taken away. {cleared.Message}");
        }

        _pendingReconcile = true;
        SaveRecord();
        return OperationResult.Success();
    }

    public async Task<OperationResult<ReconcileResult>> Reconcile()
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return OperationResultExt.Failure<ReconcileResult>(TAG, "There is nothing open to build.");
        }

        if (!_pendingReconcile && _lastReconcile is not null)
        {
            return OperationResultExt.Success(_lastReconcile);
        }

        OperationResult<ReconcileResult> reconciled;
        using (BeginLoading())
        {
            reconciled = await pipeline.Reconcile();
        }

        if (!reconciled.IsSuccessful)
        {
            return OperationResultExt.Failure<ReconcileResult>(TAG, $"What was chosen could not be built. {reconciled.Message}");
        }

        _pendingReconcile = false;
        _lastReconcile = reconciled.Result;
        RaisePositionChanged();
        return reconciled;
    }

    public void Play()
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        if (pipeline.State == MediaEngineState.Ended)
        {
            pipeline.Seek(TimeSpan.Zero);
        }

        pipeline.Play();
    }

    public void Pause()
    {
        _pipeline?.Pause();
    }

    public void Seek(TimeSpan position, SeekMode mode)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        TimeSpan duration = Duration;
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }
        else if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        CoroutineUtils.Run(async () =>
        {
            OperationResult seeked = await pipeline.Seek(position, mode);
            if (!seeked.IsSuccessful)
            {
                Logger.W(TAG, $"Seek to {position:g} ({mode}) was refused: {seeked.Message}");
            }

            if (ReferenceEquals(_pipeline, pipeline))
            {
                RaisePositionChanged();
            }
        });
    }

    public void SetVolume(double volume)
    {
        double clamped = Math.Clamp(volume, 0.0, 1.0);
        if (_volume == clamped)
        {
            return;
        }

        _volume = clamped;
        ApplyVolume();
        SaveVolumeAndMute();
        VolumeChanged?.Invoke();
    }

    public void SetMuted(bool muted)
    {
        if (_muted == muted)
        {
            return;
        }

        _muted = muted;
        ApplyVolume();
        SaveVolumeAndMute();
        VolumeChanged?.Invoke();
    }

    public void SetPlaybackRate(double rate)
    {
        throw new NotSupportedException("This engine cannot change the playback rate yet.");
    }

    private void ApplyVolume()
    {
        foreach (AudioChainEntry entry in _audioChains)
        {
            entry.Gain.SetLevel(_volume, _muted);
        }
    }

    private static IEnumerable<IPlaybackChain> ChainsOf(PlayerView view)
    {
        if (view.VideoChain is { } video)
        {
            yield return video;
        }

        foreach (ISubtitleChain subtitle in view.SubtitleChains)
        {
            yield return subtitle;
        }
    }

    #endregion

    #region Pipeline

    private PlaybackPipeline? _pipeline;

    private PlaybackPipeline EnsurePipeline()
    {
        PlaybackPipeline? existing = _pipeline;
        if (existing is not null)
        {
            return existing;
        }

        PlaybackPipeline pipeline = new()
        {
            SourceFactory = new FFmpegSourceProviderFactory(),
        };

        _components.Apply(pipeline);
        pipeline.StateChanged += Pipeline_StateChanged;
        pipeline.WarningRaised += Pipeline_WarningRaised;

        _pipeline = pipeline;
        _pendingReconcile = true;
        return pipeline;
    }

    private async Task<IVideoChain> EnsureVideoChain(PlaybackPipeline pipeline, PlayerView view)
    {
        if (view.VideoChain is { } existing)
        {
            return existing;
        }

        IVideoChain chain = await pipeline.AddVideoChain(view.EnsureVideoRenderTarget());
        view.AttachVideo(chain);
        _components.ApplyVideo(chain);
        _pendingReconcile = true;
        return chain;
    }

    private async Task<IAudioChain> CreateAudioChain(PlaybackPipeline pipeline)
    {
        IAudioChain chain = await pipeline.AddAudioChain();

        GainAudioFilter gainFilter = new();
        gainFilter.SetLevel(_volume, _muted);
        chain.Filters.Add(gainFilter);

        DownmixAudioFilter downmixFilter = new();
        chain.Filters.Add(downmixFilter);

        _components.ApplyAudio(chain);
        _audioChains.Add(new AudioChainEntry(chain, gainFilter, downmixFilter));
        return chain;
    }

    private async Task<ISubtitleChain> CreateSubtitleChain(PlaybackPipeline pipeline, PlayerView view)
    {
        ISubtitleChain chain = await pipeline.AddSubtitleChain(view.EnsureSubtitleRenderTarget());
        view.AttachSubtitle(chain);
        _components.ApplySubtitle(chain);
        _pendingReconcile = true;
        return chain;
    }

    private void Pipeline_WarningRaised(IPlaybackChain? chain, string warning)
    {
        string source = chain is null ? string.Empty : $" ({chain.Kind})";
        Logger.W(TAG, $"Playback warning{source}: {warning}");
    }

    private void Pipeline_StateChanged(MediaEngineState state, string? error)
    {
        string? message = null;
        if (state == MediaEngineState.Error)
        {
            message = error ?? "Playback failed.";
            Logger.E(TAG, $"Media playback failed: {message}", null);
        }

        CoroutineUtils.RunInMainThread(() =>
        {
            StateChanged?.Invoke(state, message);

            if (state == MediaEngineState.Ended)
            {
                CompleteProgress();
                HandleEnded();
            }
        });
    }

    #endregion

    #region Opening

    private PlaybackStateChangedEventArgs? _pendingOpen;
    private bool _isOpening;
    private MediaModel? _openedMedia;
    private string? _openedItemId;
    private long _startPositionMs;
    private long _lastProgressSaveTicks;
    private bool _volumeSaveScheduled;

    private void Playback_PlaybackStateChanged(PlaybackStateChangedEventArgs args)
    {
        SaveRecord();

        CoroutineUtils.Run(async () =>
        {
            if (_isOpening)
            {
                _pendingOpen = args;
                return;
            }

            _isOpening = true;
            try
            {
                PlaybackStateChangedEventArgs? opening = args;
                while (opening is not null)
                {
                    await OpenCurrentAsync(opening);
                    opening = _pendingOpen;
                    _pendingOpen = null;
                }
            }
            finally
            {
                _isOpening = false;
            }
        });
    }

    private async Task OpenCurrentAsync(PlaybackStateChangedEventArgs args)
    {
        SaveProgress();

        MediaModel? media = CurrentMedia;
        if (media is not null && _openedItemId == Playback.CurrentItem?.Id && Sources.Any(source => source.Locator.Value == media.Location))
        {
            return;
        }

        if (media is null)
        {
            _openedMedia = null;
            _startPositionMs = 0;
            RaiseMediaChanged(null, OperationResult.Success());
            return;
        }

        _startPositionMs = ResolveStartPositionMs(media, args);

        using IDisposable loading = BeginLoading();
        RaiseMediaChanged(media, await OpenAsync(media));
    }

    private async Task<OperationResult> OpenAsync(MediaModel media)
    {
        PlaybackPipeline pipeline = EnsurePipeline();

        OperationResult reset = await pipeline.Reset();
        if (!reset.IsSuccessful)
        {
            return reset;
        }

        _openedMedia = null;
        _openedItemId = null;

        OperationResult<MediaSource> loaded = await AddSource(media.Location);
        if (!loaded.IsSuccessful)
        {
            return OperationResult.Failure(loaded);
        }

        RecordOpen(media);

        await AddRememberedSourcesAsync(media);
        await AddSameNameSubtitlesAsync(media.Location);
        await ApplyTrackSelectionsAsync();

        if (_startPositionMs > 0)
        {
            SeekMode mode = AppSettingsModel.PreciseSeeking ? SeekMode.Precise : SeekMode.PreviousKeyframe;
            OperationResult sought = await pipeline.Seek(TimeSpan.FromMilliseconds(_startPositionMs), mode);
            if (!sought.IsSuccessful)
            {
                Logger.W(TAG, $"The position {_startPositionMs} ms could not be restored: {sought.Message}");
            }

            RaisePositionChanged();
        }

        return OperationResult.Success();
    }

    private async Task AddRememberedSourcesAsync(MediaModel media)
    {
        List<string> paths = [.. TrackSelection.PathsOf(_loadedSelection), .. TrackSelection.PathsOf(TrackSelection.Load(media))];
        foreach (string path in paths.Distinct())
        {
            if (Sources.Any(source => source.Locator.Value == path))
            {
                continue;
            }

            OperationResult<MediaSource> added = await AddSource(path);
            if (!added.IsSuccessful)
            {
                Logger.W(TAG, $"A remembered track could not be restored from '{path}'. {added.Message}");
            }
        }
    }

    private async Task AddSameNameSubtitlesAsync(string mediaPath)
    {
        foreach (string path in await FindSameNameSubtitlesAsync(mediaPath))
        {
            if (Sources.Any(source => source.Locator.Value == path))
            {
                continue;
            }

            OperationResult<MediaSource> added = await AddSource(path);
            if (!added.IsSuccessful)
            {
                Logger.W(TAG, $"'{path}' could not be imported: {added.Message}");
            }
        }
    }

    private static async Task<IReadOnlyList<string>> FindSameNameSubtitlesAsync(string mediaPath)
    {
        if (string.IsNullOrEmpty(mediaPath))
        {
            return [];
        }

        string? folder = Path.GetDirectoryName(mediaPath);
        string stem = Path.GetFileNameWithoutExtension(mediaPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(stem))
        {
            return [];
        }

        List<string> candidates = await TaskDispatcher.DefaultThreadPool.Submit(() =>
        {
            List<string> found = [];
            try
            {
                foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!AppInfoProvider.IsSupportedSubtitleExtension(Path.GetExtension(path)))
                    {
                        continue;
                    }

                    if (string.Equals(Path.GetFileNameWithoutExtension(path), stem, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(path);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.W(TAG, $"The folder '{folder}' could not be listed for subtitles: {ex.Message}");
            }

            return found;
        });

        return [.. candidates.OrderBy(path => StringUtils.SmartFileNameKeySelector(Path.GetFileName(path)), StringUtils.SmartFileNameComparer)];
    }

    private void RaiseMediaChanged(MediaModel? media, OperationResult result)
    {
        MediaChanged?.Invoke(media, result);
    }

    private void HandleEnded()
    {
        if (Playback.RepeatMode == RepeatMode.One)
        {
            Play();
            return;
        }

        if (AppSettingsModel.AutoPlayNext)
        {
            Playback.Next();
        }
    }

    private static long ResolveStartPositionMs(MediaModel media, PlaybackStateChangedEventArgs args)
    {
        if (args.InitialPositionMs >= 0)
        {
            return args.InitialPositionMs;
        }

        switch (args.Reason)
        {
            case PlaybackStateChangeReason.Next:
            case PlaybackStateChangeReason.Previous:
            case PlaybackStateChangeReason.PreviousByOverScroll:
                return 0;

            default:
                {
                    if (!AppSettingsModel.RestoreLastPlaybackPosition)
                    {
                        return 0;
                    }

                    long positionMs = media.LastPositionMs;
                    long remainingMs = media.DurationMs - positionMs;
                    return remainingMs >= 0 && remainingMs < AlmostEndedThresholdMilliseconds ? 0 : positionMs;
                }
        }
    }

    #endregion

    #region Media record

    private void RecordOpen(MediaModel media)
    {
        _openedMedia = media;
        _openedItemId = Playback.CurrentItem?.Id;

        CoroutineUtils.Run(async () =>
        {
            await media.SetAsVisited();
            if (AppSettingsModel.SaveBrowsingHistory)
            {
                await MediaHistoryItemModel.AddAsync(media.Id, media.Title1);
            }
        });
    }

    public void SaveProgress()
    {
        MediaModel? media = _openedMedia;
        if (media is null || _pipeline is null)
        {
            return;
        }

        _lastProgressSaveTicks = Environment.TickCount64;

        long durationMs = Duration.Ticks / TimeSpan.TicksPerMillisecond;
        long positionMs = Position.Ticks / TimeSpan.TicksPerMillisecond;

        if (durationMs > 0 && media.DurationMs <= 0)
        {
            CoroutineUtils.Run(async () => await media.SetDuration(durationMs));
        }

        int progress = durationMs > 0 ? (int)Math.Clamp(positionMs * 100 / durationMs, 0, 100) : -1;
        CoroutineUtils.Run(async () => await media.SetProgress(progress, positionMs));
    }

    public void CompleteProgress()
    {
        MediaModel? media = _openedMedia;
        if (media is not null)
        {
            CoroutineUtils.Run(async () => await media.SetProgress(100, 0));
        }
    }

    public void SetFavorite(bool isFavorite)
    {
        MediaModel? media = _openedMedia;
        if (media is null)
        {
            return;
        }

        if (isFavorite)
        {
            FavoriteModel.Instance.Add(media.Id, media.Title1, true);
        }
        else
        {
            FavoriteModel.Instance.RemoveWithId(media.Id, true);
        }
    }

    private void SaveVolumeAndMute()
    {
        if (_volumeSaveScheduled)
        {
            return;
        }

        _volumeSaveScheduled = true;
        CoroutineUtils.Run(async () =>
        {
            await Task.Delay(VolumeSaveDelayMilliseconds);
            _volumeSaveScheduled = false;

            AppSettingsModel.PlayerVolume = _volume;
            AppSettingsModel.PlayerMuted = _muted;
        });
    }

    #endregion

    #region Audio device change

    private void AudioDevices_Changed()
    {
        CoroutineUtils.Run(RefreshAudioOutputAsync);
    }

    private async Task RefreshAudioOutputAsync()
    {
        if (_disposed)
        {
            return;
        }

        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null || _audioChains.Count == 0 || _audioChains[0].Chain.SelectedStream is null)
        {
            return;
        }

        if (pipeline.State is not (MediaEngineState.Playing or MediaEngineState.Paused or MediaEngineState.Ended))
        {
            return;
        }

        _components.Apply(pipeline);

        OperationResult<ReconcileResult> reconciled = await pipeline.Reconcile();
        if (!reconciled.IsSuccessful)
        {
            _pendingReconcile = true;
            Logger.W(TAG, $"The audio output could not be re-resolved: {reconciled.Message}");
        }
        else if (reconciled.Result.Audio() is { IsSuccessful: false } audioFailure)
        {
            Logger.W(TAG, $"The audio output could not be re-resolved: {audioFailure.Message}");
        }
        else
        {
            _pendingReconcile = false;
            _lastReconcile = reconciled.Result;
        }
    }

    #endregion

    #region Position timer

    private DispatcherQueueTimer? _positionTimer;

    private void StartPositionTimer()
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null || _positionTimer is not null)
        {
            return;
        }

        _positionTimer = dispatcher.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(PositionIntervalMilliseconds);
        _positionTimer.Tick += PositionTimer_Tick;
        _positionTimer.Start();
    }

    private void StopPositionTimer()
    {
        DispatcherQueueTimer? timer = _positionTimer;
        _positionTimer = null;
        if (timer is null)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= PositionTimer_Tick;
    }

    private void PositionTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (State != MediaEngineState.Playing)
        {
            return;
        }

        if (Environment.TickCount64 - _lastProgressSaveTicks >= ProgressSaveIntervalMilliseconds)
        {
            SaveProgress();
        }

        RaisePositionChanged();
    }

    #endregion

    #region Record

    private async Task ApplyTrackSelectionsAsync()
    {
        if (_openedMedia is not { } media)
        {
            return;
        }

        TrackSelectionModel? mediaSelection = TrackSelection.Load(media);
        foreach (MediaStreamInfo stream in TrackSelection.Resolve(_loadedSelection, mediaSelection, 0, true, MediaStreamKind.Audio, Sources))
        {
            OperationResult added = await AddAudioStream(stream);
            if (!added.IsSuccessful)
            {
                Logger.W(TAG, $"The sound of track #{stream.Index} could not be chosen: {added.Message}");
            }
        }

        foreach (PlayerView view in (PlayerView[])[.. _views])
        {
            await ApplyViewTracksAsync(view);
        }

        _loadedSelection = null;
    }

    private async Task ApplyViewTracksAsync(PlayerView view)
    {
        int index = _views.IndexOf(view);
        if (index < 0 || _openedMedia is not { } media)
        {
            return;
        }

        TrackSelectionModel? mediaSelection = TrackSelection.Load(media);
        bool opening = index == 0;

        foreach (MediaStreamInfo stream in TrackSelection.Resolve(_loadedSelection, mediaSelection, index, opening, MediaStreamKind.Video, Sources))
        {
            OperationResult chosen = await SelectVideoStream(view, stream);
            if (!chosen.IsSuccessful)
            {
                Logger.W(TAG, $"The picture of track #{stream.Index} could not be chosen for view {index}: {chosen.Message}");
            }
        }

        foreach (MediaStreamInfo stream in TrackSelection.Resolve(_loadedSelection, mediaSelection, index, opening, MediaStreamKind.Subtitle, Sources))
        {
            OperationResult chosen = await AddSubtitleStream(view, stream);
            if (!chosen.IsSuccessful)
            {
                Logger.W(TAG, $"The subtitles of track #{stream.Index} could not be chosen for view {index}: {chosen.Message}");
            }
        }
    }

    private void SaveRecord()
    {
        if (!_loaded || _views.Count == 0 || ServiceManager.GetService<IApplicationService>().Exiting)
        {
            return;
        }

        TrackSelectionModel selection = DescribeSelection();
        PlayerSessionRecord.Save(Id, new PlayerSessionRecord()
        {
            PlayQueue = _playQueue.ToJsonModel(),
            Playback = Playback.ToJsonModel(),
            TrackSelection = selection,
        });

        // Only a session showing something may rewrite the media's default: saving "nothing chosen yet" over it would
        // erase the tracks the next open is supposed to start from.
        if (_openedMedia is { } media && HasOpinion(selection))
        {
            TrackSelection.Save(media, selection);
        }
    }

    private TrackSelectionModel DescribeSelection()
    {
        TrackSelectionModel model = _openedMedia is { } media
            ? TrackSelection.Load(media) ?? new TrackSelectionModel()
            : new TrackSelectionModel();

        // The menu preference is still the media's, so every view entry mirrors it until views own one of their own.
        bool allowMultiSubtitleTracks = model.Views is { Count: > 0 } existing ? existing[0].AllowMultiSubtitleTracks : false;

        List<ViewTrackSelectionModel> views = [];
        foreach (PlayerView view in _views)
        {
            ViewTrackSelectionModel entry = new()
            {
                AllowMultiSubtitleTracks = allowMultiSubtitleTracks,
            };

            if (view.VideoChain is { } video)
            {
                entry.VideoSelection = video.SelectedStream is { } stream
                    ? new SelectedTrackModel { Enabled = true, Path = PathOf(stream), Index = stream.Index }
                    : new SelectedTrackModel { Enabled = false };
            }

            if (view.SubtitleChains.Count > 0)
            {
                entry.SubtitleSelections = [.. view.SubtitleChains
                    .Select(chain => chain.SelectedStream)
                    .OfType<SubtitleStreamInfo>()
                    .Select(stream => new TrackSelectionEntryModel { Path = PathOf(stream), Index = stream.Index })];
            }

            views.Add(entry);
        }

        model.Views = views;

        model.AudioSelections = _audioChains.Count == 0
            ? null
            : [.. _audioChains
                .Select(entry => entry.Chain.SelectedStream)
                .OfType<AudioStreamInfo>()
                .Select(stream => new TrackSelectionEntryModel { Path = PathOf(stream), Index = stream.Index })];

        return model;
    }

    private static bool HasOpinion(TrackSelectionModel selection)
    {
        if (selection.AudioSelections is not null || selection.Views is not { } views)
        {
            return true;
        }

        return views.Any(view => view.VideoSelection is not null || view.SubtitleSelections is not null);
    }

    private string PathOf(MediaStreamInfo stream)
    {
        foreach (MediaSource source in Sources)
        {
            if (source.Info.Streams.Any(candidate => candidate.Equals(stream)))
            {
                return source.Locator.Value;
            }
        }

        return string.Empty;
    }

    private void Playback_PlayQueueChanged()
    {
        SaveRecord();
    }

    #endregion

    #region Loading state

    private int _loadingCount;

    internal IDisposable BeginLoading()
    {
        if (Interlocked.Increment(ref _loadingCount) == 1)
        {
            RaiseLoadingChanged();
        }

        return new LoadingSession(this);
    }

    private void RaiseLoadingChanged()
    {
        CoroutineUtils.RunInMainThread(() => LoadingChanged?.Invoke());
    }

    private sealed partial class LoadingSession(PlayerSession session) : IDisposable
    {
        private bool _ended;

        public void Dispose()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;

            if (Interlocked.Decrement(ref session._loadingCount) == 0)
            {
                session.RaiseLoadingChanged();
            }
        }
    }

    #endregion

    #region Misc

    private void RaisePositionChanged()
    {
        CoroutineUtils.RunInMainThread(() => PositionChanged?.Invoke());
    }

    #endregion
}
