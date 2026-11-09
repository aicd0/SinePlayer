// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Playback.Core;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Pipeline;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;
using SinePlayer.Views.Pages.Player;

using Windows.Storage;

namespace SinePlayer.Helpers.MenuFlyoutHelpers;

internal sealed class PlayerMenuContext
{
    // Temporary workaround for the lack of a proper settings system. This is a static field to avoid having to pass the setting around in the constructor, but it should be replaced with a proper settings system that can be injected into the class.
    private static bool _allowMultiAudioTracks = false;

    public required int WindowId { get; init; }

    public required ActionHandler ActionHandler { get; init; }

    public required MediaModel Media { get; init; }

    public required PlayerSettingsModel Settings { get; init; }

    public required Action<Action<PlayerSettingsModel>> UpdateSettings { get; init; }

    public PlayQueueModel.Builder? PlayQueue { get; init; }

    public PlaybackModel.Builder? Playback { get; init; }

    public string? Url { get; init; }

    public PlayerSession? Session { get; init; }

    public PlayerView? View { get; init; }

    public Action<string>? PromptInfo { get; init; }

    public IReadOnlyList<MediaSource> Sources => Session?.Sources ?? [];

    public bool AllowMultiAudioTracks => _allowMultiAudioTracks;

    public IReadOnlyList<MediaStreamInfo>? Tracks(MediaStreamKind kind)
    {
        IReadOnlyList<MediaSource> sources = Sources;
        if (sources.Count == 0)
        {
            return null;
        }

        List<MediaStreamInfo> streams = [];
        foreach (MediaSource source in sources)
        {
            streams.AddRange(StreamsOf(source, kind));
        }

        return streams;
    }

    public MediaStreamInfo? SelectedTrack(MediaStreamKind kind)
    {
        return kind switch
        {
            MediaStreamKind.Video => View?.PlaybackInfo.SelectedVideoStream,
            MediaStreamKind.Audio => View?.PlaybackInfo.SelectedAudioStream,
            MediaStreamKind.Subtitle => View?.PlaybackInfo.SelectedSubtitleStream,
            _ => null,
        };
    }

    public bool IsAudioTrackPlaying(MediaStreamInfo stream)
    {
        return Session?.AudioChains.Any(chain => chain.SelectedStream?.Equals(stream) == true) == true;
    }

    public void SetAllowMultiAudioTracks(bool allow)
    {
        _allowMultiAudioTracks = allow;

        PlayerSession? session = Session;
        if (allow || session is not { } target)
        {
            return;
        }

        MediaStreamInfo[] extra = [.. target.AudioChains.Select(chain => chain.SelectedStream).OfType<MediaStreamInfo>().Skip(1)];
        if (extra.Length == 0)
        {
            return;
        }

        CoroutineUtils.Run(async () =>
        {
            foreach (MediaStreamInfo stream in extra)
            {
                await target.RemoveAudioStream(stream);
            }

            await target.Reconcile();
        });
    }

    public void ToggleAudioTrack(MediaStreamInfo stream)
    {
        PlayerSession? session = Session;
        if (session is null)
        {
            return;
        }

        bool playing = IsAudioTrackPlaying(stream);
        CoroutineUtils.Run(async () =>
        {
            OperationResult changed = playing ? await session.RemoveAudioStream(stream) : await session.AddAudioStream(stream);
            if (!changed.IsSuccessful)
            {
                PromptInfo?.Invoke(changed.Message);
                return;
            }

            OperationResult<ReconcileResult> reconciled = await session.Reconcile();
            if (!reconciled.IsSuccessful)
            {
                PromptInfo?.Invoke(reconciled.Message);
                return;
            }

            OperationResult? built = reconciled.Result.Audio();
            if (built is { IsSuccessful: false })
            {
                PromptInfo?.Invoke(built.Message);
                return;
            }

            TrackSelection.Save(Media, MediaStreamKind.Audio, SourceOf(stream), stream);
        });
    }

    public void ClearAudioTracks()
    {
        PlayerSession? session = Session;
        if (session is null)
        {
            return;
        }

        MediaStreamInfo[] playing = [.. session.AudioChains.Select(chain => chain.SelectedStream).OfType<MediaStreamInfo>()];
        CoroutineUtils.Run(async () =>
        {
            foreach (MediaStreamInfo stream in playing)
            {
                OperationResult removed = await session.RemoveAudioStream(stream);
                if (!removed.IsSuccessful)
                {
                    PromptInfo?.Invoke(removed.Message);
                    return;
                }
            }

            OperationResult<ReconcileResult> reconciled = await session.Reconcile();
            if (!reconciled.IsSuccessful)
            {
                PromptInfo?.Invoke(reconciled.Message);
            }
        });
    }

    public void AddSubtitleTracks()
    {
        PlayerSession? session = Session;
        PlayerView? view = View;
        if (session is null || view is null)
        {
            return;
        }

        CoroutineUtils.Run(async () =>
        {
            IReadOnlyList<StorageFile> files = await FilePickerUtils.PickFiles(WindowId, AppInfoProvider.SupportedSubtitleExtensions);
            if (files.Count == 0)
            {
                return;
            }

            OperationResult added = await SubtitleSourceLoader.AddAndSelect(session, view, Media, [.. files.Select(file => file.Path)]);
            if (!added.IsSuccessful)
            {
                PromptInfo?.Invoke(added.Message);
            }
        });
    }

    public MediaSource? SourceOf(MediaStreamInfo? stream)
    {
        if (stream is null)
        {
            return null;
        }

        foreach (MediaSource source in Sources)
        {
            if (source.Info.Streams.Any(candidate => candidate.Equals(stream)))
            {
                return source;
            }
        }

        return null;
    }

    public void SelectTrack(MediaStreamKind kind, MediaStreamInfo? stream)
    {
        PlayerSession? session = Session;
        PlayerView? view = View;
        if (session is null || view is null)
        {
            return;
        }

        CoroutineUtils.Run(async () =>
        {
            OperationResult selected = kind switch
            {
                MediaStreamKind.Video => await session.SelectVideoStream(view, stream),
                MediaStreamKind.Audio => await SelectSoleAudioStream(session, stream),
                _ => await SelectSoleSubtitleStream(session, view, stream),
            };
            if (!selected.IsSuccessful)
            {
                PromptInfo?.Invoke(selected.Message);
                return;
            }

            OperationResult<ReconcileResult> reconciled = await session.Reconcile();
            if (!reconciled.IsSuccessful)
            {
                PromptInfo?.Invoke(reconciled.Message);
                return;
            }

            OperationResult? built = kind switch
            {
                MediaStreamKind.Video => reconciled.Result.Video(view),
                MediaStreamKind.Audio => reconciled.Result.Audio(),
                _ => reconciled.Result.Subtitle(view),
            };
            if (built is { IsSuccessful: false })
            {
                PromptInfo?.Invoke(built.Message);
                return;
            }

            TrackSelection.Save(Media, kind, SourceOf(stream), stream);
        });
    }

    private static async Task<OperationResult> SelectSoleAudioStream(PlayerSession session, MediaStreamInfo? stream)
    {
        MediaStreamInfo[] playing = [.. session.AudioChains.Select(chain => chain.SelectedStream).OfType<MediaStreamInfo>()];
        foreach (MediaStreamInfo candidate in playing)
        {
            if (stream is not null && candidate.Equals(stream))
            {
                continue;
            }

            OperationResult removed = await session.RemoveAudioStream(candidate);
            if (!removed.IsSuccessful)
            {
                return removed;
            }
        }

        return stream is null ? OperationResult.Success() : await session.AddAudioStream(stream);
    }

    private static async Task<OperationResult> SelectSoleSubtitleStream(PlayerSession session, PlayerView view, MediaStreamInfo? stream)
    {
        MediaStreamInfo[] selected = [.. view.SubtitleChains.Select(chain => chain.SelectedStream).OfType<MediaStreamInfo>()];
        foreach (MediaStreamInfo candidate in selected)
        {
            if (stream is not null && candidate.Equals(stream))
            {
                continue;
            }

            OperationResult removed = await session.RemoveSubtitleStream(view, candidate);
            if (!removed.IsSuccessful)
            {
                return removed;
            }
        }

        return stream is null ? OperationResult.Success() : await session.AddSubtitleStream(view, stream);
    }

    private static IReadOnlyList<MediaStreamInfo> StreamsOf(MediaSource source, MediaStreamKind kind)
    {
        return kind switch
        {
            MediaStreamKind.Video => source.Info.VideoStreams,
            MediaStreamKind.Audio => source.Info.AudioStreams,
            MediaStreamKind.Subtitle => source.Info.SubtitleStreams,
            _ => [],
        };
    }
}
