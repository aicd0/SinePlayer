// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Threading;

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.CoreEngine;

/// <summary>
/// The handle <see cref="IMediaEngine.CreatePlaybackInfo"/> hands out. It holds the pipeline it describes and nothing
/// else — not the engine, not the page — so what a surface keeps can only ever reach a pipeline the engine has said
/// is current.
/// <para>
/// The engine attaches the pipeline it has just made and detaches it before that pipeline is disposed, so after the
/// player is gone every member answers "nothing" without touching anything: no disposed object is called, and no
/// field of one is read. The field is written by the engine on its own thread and read by whichever surface is
/// refreshing, which is why it is a volatiled reference rather than a plain one.
/// </para>
/// </summary>
internal sealed class PlaybackInfoHandle : IPlaybackInfoProvider
{
    private PlaybackPipeline? _pipeline;

    /// <summary>Says which pipeline this handle describes. Called by the engine that owns it, before anything reads.</summary>
    internal void Attach(PlaybackPipeline pipeline)
    {
        Volatile.Write(ref _pipeline, pipeline);
    }

    /// <summary>
    /// Says this handle describes nothing. Called by the engine before it disposes the pipeline, which is what makes
    /// the handle safe to hold afterwards.
    /// </summary>
    internal void Detach()
    {
        Volatile.Write(ref _pipeline, null);
    }

    public MediaInfo? Media => Volatile.Read(ref _pipeline)?.MediaInfo;

    public MediaEngineState State => Volatile.Read(ref _pipeline)?.State ?? MediaEngineState.None;

    public string? Error => Volatile.Read(ref _pipeline)?.Error;

    public TimeSpan Position => Volatile.Read(ref _pipeline)?.Position ?? TimeSpan.Zero;

    public long PresentedFrames => Volatile.Read(ref _pipeline)?.PresentedFrameCount ?? 0;

    public long DroppedFrames => Volatile.Read(ref _pipeline)?.DroppedFrameCount ?? 0;

    public long? DisplayedFrames => Volatile.Read(ref _pipeline)?.DisplayedFrameCount;

    public TimeSpan PictureLag => Volatile.Read(ref _pipeline)?.PictureLag ?? TimeSpan.Zero;

    public double AverageDecodeMilliseconds => Volatile.Read(ref _pipeline)?.AverageDecodeMilliseconds ?? 0;

    public double AverageRenderMilliseconds => Volatile.Read(ref _pipeline)?.AverageRenderMilliseconds ?? 0;

    public string? StarvedFor => Volatile.Read(ref _pipeline)?.StarvedFor;

    public double Volume => Volatile.Read(ref _pipeline)?.Volume ?? 0;

    public bool IsMuted => Volatile.Read(ref _pipeline)?.IsMuted ?? false;

    public bool IsVideoHardwareAccelerated => Volatile.Read(ref _pipeline)?.IsVideoHardwareAccelerated ?? false;

    public bool IsAudioHardwareAccelerated => Volatile.Read(ref _pipeline)?.IsAudioHardwareAccelerated ?? false;
}
