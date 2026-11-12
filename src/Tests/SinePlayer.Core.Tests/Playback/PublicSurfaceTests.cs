// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Reflection;

using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The host surface of the playback engine, as a list that has to be edited on purpose.
/// <para>
/// What this protects against is drift: a type that becomes public because a public member mentions it, and then
/// stays public because nothing ever says otherwise. On its own that costs nothing; a hundred of them make the
/// engine impossible to change, because every one is a promise. The list below is the promise, and a change to it
/// is a decision someone made rather than a side effect of an edit somewhere else.
/// </para>
/// <para>
/// The rules the entries are meant to satisfy are in <c>docs/PlaybackStructureRedesign.md</c>.
/// </para>
/// </summary>
[TestFixture]
public class PublicSurfaceTests
{
    private const string EngineNamespace = "SinePlayer.Core.Playback";

    /// <summary>
    /// Every type <c>SinePlayer.Core.Playback</c> exports, sorted ordinally. Each one is something a host may
    /// compile against, so an addition is a widening of the contract and a removal is a breaking change.
    /// </summary>
    private static readonly string[] Expected =
    [
        "SinePlayer.Core.Playback.Abstractions.AudioSampleFormat",
        "SinePlayer.Core.Playback.Abstractions.ChannelLayout",
        "SinePlayer.Core.Playback.Abstractions.Decode.DecoderOptions",
        "SinePlayer.Core.Playback.Abstractions.Decode.IAudioDecoder",
        "SinePlayer.Core.Playback.Abstractions.Decode.IGpuVideoBuffer",
        "SinePlayer.Core.Playback.Abstractions.Decode.IMediaDecoder",
        "SinePlayer.Core.Playback.Abstractions.Decode.IPlanarAudioBuffer",
        "SinePlayer.Core.Playback.Abstractions.Decode.IPlanarVideoBuffer",
        "SinePlayer.Core.Playback.Abstractions.Decode.ISubtitleDecoder",
        "SinePlayer.Core.Playback.Abstractions.Decode.IVideoDecoder",
        "SinePlayer.Core.Playback.Abstractions.Decode.RawAudioBuffer",
        "SinePlayer.Core.Playback.Abstractions.Decode.RawVideoFrame",
        "SinePlayer.Core.Playback.Abstractions.Decode.SubtitleChange",
        "SinePlayer.Core.Playback.Abstractions.Decode.SubtitleCue",
        "SinePlayer.Core.Playback.Abstractions.Decode.SubtitleCueFormat",
        "SinePlayer.Core.Playback.Abstractions.Decode.SubtitleImage",
        "SinePlayer.Core.Playback.Abstractions.Filter.IAudioFilter",
        "SinePlayer.Core.Playback.Abstractions.Filter.IMediaFilter",
        "SinePlayer.Core.Playback.Abstractions.Filter.IVideoFilter",
        "SinePlayer.Core.Playback.Abstractions.ID3D11GpuDevice",
        "SinePlayer.Core.Playback.Abstractions.IGpuDevice",
        "SinePlayer.Core.Playback.Abstractions.IMediaBuffer",
        "SinePlayer.Core.Playback.Abstractions.ISubtitleImageBuffer",
        "SinePlayer.Core.Playback.Abstractions.Info.AudioStreamInfo",
        "SinePlayer.Core.Playback.Abstractions.Info.ColourInfo",
        "SinePlayer.Core.Playback.Abstractions.Info.ColourPrimaries",
        "SinePlayer.Core.Playback.Abstractions.Info.ColourRange",
        "SinePlayer.Core.Playback.Abstractions.Info.ColourTransfer",
        "SinePlayer.Core.Playback.Abstractions.Info.MediaAttachment",
        "SinePlayer.Core.Playback.Abstractions.Info.MediaInfo",
        "SinePlayer.Core.Playback.Abstractions.Info.MediaStreamInfo",
        "SinePlayer.Core.Playback.Abstractions.Info.SubtitleStreamInfo",
        "SinePlayer.Core.Playback.Abstractions.Info.VideoStreamInfo",
        "SinePlayer.Core.Playback.Abstractions.MediaStreamKind",
        "SinePlayer.Core.Playback.Abstractions.PixelFormat",
        "SinePlayer.Core.Playback.Abstractions.Registry.IAudioDecoderFactory",
        "SinePlayer.Core.Playback.Abstractions.Registry.IAudioRendererFactory",
        "SinePlayer.Core.Playback.Abstractions.Registry.IComponentCreator`1",
        "SinePlayer.Core.Playback.Abstractions.Registry.IComponentFactory",
        "SinePlayer.Core.Playback.Abstractions.Registry.IComponentRegistry",
        "SinePlayer.Core.Playback.Abstractions.Registry.ISourceProviderFactory",
        "SinePlayer.Core.Playback.Abstractions.Registry.ISubtitleDecoderFactory",
        "SinePlayer.Core.Playback.Abstractions.Registry.ISubtitleRendererFactory",
        "SinePlayer.Core.Playback.Abstractions.Registry.IVideoDecoderFactory",
        "SinePlayer.Core.Playback.Abstractions.Registry.IVideoFilterFactory",
        "SinePlayer.Core.Playback.Abstractions.Registry.IVideoRendererFactory",
        "SinePlayer.Core.Playback.Abstractions.Render.AudioSubmitYield",
        "SinePlayer.Core.Playback.Abstractions.Render.IAudioRenderer",
        "SinePlayer.Core.Playback.Abstractions.Render.IAudioSubmit",
        "SinePlayer.Core.Playback.Abstractions.Render.ISubtitleRenderer",
        "SinePlayer.Core.Playback.Abstractions.Render.IVideoRenderTarget",
        "SinePlayer.Core.Playback.Abstractions.Render.IVideoRenderer",
        "SinePlayer.Core.Playback.Abstractions.Render.SubtitleFrame",
        "SinePlayer.Core.Playback.Abstractions.Render.SubtitleInsets",
        "SinePlayer.Core.Playback.Abstractions.Render.SubtitlePresentation",
        "SinePlayer.Core.Playback.Abstractions.Render.VideoOrientation",
        "SinePlayer.Core.Playback.Abstractions.Render.VideoResizeMode",
        "SinePlayer.Core.Playback.Abstractions.Render.VideoScalingMode",
        "SinePlayer.Core.Playback.Abstractions.Render.VideoSurfacePreference",
        "SinePlayer.Core.Playback.Abstractions.Render.VideoTransform",
        "SinePlayer.Core.Playback.Abstractions.SeekMode",
        "SinePlayer.Core.Playback.Abstractions.Source.IMediaSource",
        "SinePlayer.Core.Playback.Abstractions.Source.MediaLocator",
        "SinePlayer.Core.Playback.Abstractions.Source.MediaPacket",
        "SinePlayer.Core.Playback.Abstractions.Source.MediaSource",
        "SinePlayer.Core.Playback.Abstractions.Timing.FrameSyncAction",
        "SinePlayer.Core.Playback.Abstractions.Timing.IPlaybackClock",
        "SinePlayer.Core.Playback.Abstractions.Timing.ISynchronizer",
        "SinePlayer.Core.Playback.Backends.Direct3D11.D3D11VideoRendererFactory",
        "SinePlayer.Core.Playback.Backends.Downmix.DownmixAudioFilter",
        "SinePlayer.Core.Playback.Backends.FFmpeg.FFmpegAudioDecoderFactory",
        "SinePlayer.Core.Playback.Backends.FFmpeg.FFmpegRuntime",
        "SinePlayer.Core.Playback.Backends.FFmpeg.FFmpegSourceProviderFactory",
        "SinePlayer.Core.Playback.Backends.FFmpeg.FFmpegSubtitleDecoderFactory",
        "SinePlayer.Core.Playback.Backends.FFmpeg.FFmpegVideoDecoderFactory",
        "SinePlayer.Core.Playback.Backends.Gain.GainAudioFilter",
        "SinePlayer.Core.Playback.Backends.Libass.AssSubtitleRendererFactory",
        "SinePlayer.Core.Playback.Backends.Null.NullAudioDecoderFactory",
        "SinePlayer.Core.Playback.Backends.Null.NullAudioRendererFactory",
        "SinePlayer.Core.Playback.Backends.Null.NullSubtitleDecoderFactory",
        "SinePlayer.Core.Playback.Backends.Null.NullSubtitleRendererFactory",
        "SinePlayer.Core.Playback.Backends.Null.NullVideoDecoderFactory",
        "SinePlayer.Core.Playback.Backends.Null.NullVideoRendererFactory",
        "SinePlayer.Core.Playback.Backends.Wasapi.WasapiAudioEndpoint",
        "SinePlayer.Core.Playback.Backends.Wasapi.WasapiAudioEndpoints",
        "SinePlayer.Core.Playback.Backends.Wasapi.WasapiAudioRendererFactory",
        "SinePlayer.Core.Playback.Backends.Win2D.SubtitleFonts",
        "SinePlayer.Core.Playback.Backends.Win2D.Win2DSubtitleRendererFactory",
        "SinePlayer.Core.Playback.Hosting.AudioFilterChain",
        "SinePlayer.Core.Playback.Hosting.AudioSpeedAlgorithm",
        "SinePlayer.Core.Playback.Hosting.IAudioChain",
        "SinePlayer.Core.Playback.Hosting.IPictureFrameSource",
        "SinePlayer.Core.Playback.Hosting.IPlaybackChain",
        "SinePlayer.Core.Playback.Hosting.IPlaybackChain`1",
        "SinePlayer.Core.Playback.Hosting.ISubtitleChain",
        "SinePlayer.Core.Playback.Hosting.IVideoChain",
        "SinePlayer.Core.Playback.Hosting.MediaEngineState",
        "SinePlayer.Core.Playback.Hosting.PlaybackPipeline",
        "SinePlayer.Core.Playback.Hosting.ReconcileResult",
        "SinePlayer.Core.Playback.Selection.AudioComponentResolver",
        "SinePlayer.Core.Playback.Selection.SubtitleComponentResolver",
        "SinePlayer.Core.Playback.Selection.VideoComponentResolver",
        "SinePlayer.Core.Playback.Selection.VideoPreferences",
        "SinePlayer.Core.Playback.Thumbnails.MediaThumbnail",
        "SinePlayer.Core.Playback.Thumbnails.MediaThumbnailExtractor",
    ];

    [Test]
    public void ExportsOnlyTheHostSurface()
    {
        Assembly engine = typeof(PlaybackPipeline).Assembly;

        List<string> actual = [.. engine.GetExportedTypes()
            .Select(type => type.FullName ?? type.Name)
            .Where(name => name.StartsWith(EngineNamespace, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)];

        // A scan that finds nothing proves nothing, so the namespace is checked for having a surface at all.
        Assert.That(actual, Is.Not.Empty, $"{engine.GetName().Name} exports nothing under {EngineNamespace}, so this test would pass vacuously");

        Assert.That(
            actual,
            Is.EqualTo(Expected),
            "The surface of " + EngineNamespace + " changed. Every entry is a promise to hosts: add one only when a host"
            + " has to compile against it (docs/PlaybackStructureRedesign.md, R1).");
    }
}
