// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Playback.Core;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Views.Pages.Sidebar.PlaybackInfo;

internal sealed partial class PlaybackInfoPageViewModel : BaseViewModel, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isEmpty = true;
    public bool IsEmpty
    {
        get => _isEmpty;
        set
        {
            if (_isEmpty == value)
            {
                return;
            }

            _isEmpty = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasContent)));
        }
    }

    public bool HasContent => !_isEmpty;
    public IReadOnlyList<PlaybackInfoRow> Rows { get; private set; } = [];

    public void Update(PlaybackInfoHandle? info)
    {
        if (info?.Media is not { } media)
        {
            IsEmpty = true;
            Rows = [];
            return;
        }

        IsEmpty = false;

        List<PlaybackInfoRow> rows = [];
        void Add(string key, string name, string value)
        {
            rows.Add(PlaybackInfoRow.Property(key, name, value));
        }

        void Group(string key, string name)
        {
            rows.Add(PlaybackInfoRow.Group(key, name));
        }

        Group("group.media", StringResource.Media);
        Add("media.container", StringResource.Container, media.ContainerId);
        Add("media.duration", StringResource.Duration, FormatTime(media.Duration));
        if (media.Bitrate > 0)
        {
            Add("media.bitrate", StringResource.BitRate, FormatBitrate(media.Bitrate));
        }

        if (info.VideoChain?.SelectedStream is { } video)
        {
            Group("group.video", StringResource.Video);
            Add("video.codec", StringResource.Codec, FormatCodec(video));
            Add("video.resolution", StringResource.Resolution, video.Width + " x " + video.Height);
            if (video.FrameRate > 0)
            {
                Add("video.framerate", StringResource.FrameRate, FormatNumber(video.FrameRate) + " fps");
            }

            Add("video.pixelformat", StringResource.PixelFormat, video.PixelFormat.ToString().ToLowerInvariant());
            if (video.BitDepth > 0)
            {
                Add("video.bitdepth", StringResource.BitDepth, FormatNumber(video.BitDepth));
            }

            if (video.Bitrate > 0)
            {
                Add("video.bitrate", StringResource.BitRate, FormatBitrate(video.Bitrate));
            }

            Add("video.decoder", StringResource.Decoder, info.VideoDecoderName ?? StringResource.None);
            Add("video.decoder.mode", StringResource.DecodeMode, info.IsVideoHardwareAccelerated ? StringResource.Hardware : StringResource.Software);
            Add("video.renderer", StringResource.Renderer, info.VideoRendererName ?? StringResource.None);
            Add("video.presented", StringResource.PresentedFrames, FormatNumber(info.PresentedFrames));
            Add("video.dropped", StringResource.DroppedFrames, FormatNumber(info.DroppedFrames));
            Add("video.lag", StringResource.PictureLag, FormatMilliseconds(info.PictureLag.TotalMilliseconds));
            Add("video.decodetime", StringResource.VideoDecodeTime, FormatMilliseconds(info.AverageVideoDecodeMilliseconds));
            Add("video.rendertime", StringResource.VideoRenderTime, FormatMilliseconds(info.AverageVideoRenderMilliseconds));
        }

        IAudioChain[] audioChains = [.. info.AudioChains.Where(chain => chain.SelectedStream is not null)];
        for (int index = 0; index < audioChains.Length; index++)
        {
            IAudioChain chain = audioChains[index];
            if (chain.SelectedStream is not { } audio)
            {
                continue;
            }

            string suffix = index == 0 ? string.Empty : $" #{index + 1}";
            string key = $"audio#{index + 1}";
            Group($"group.{key}", StringResource.Audio + suffix);
            Add($"{key}.codec", StringResource.Codec, FormatCodec(audio));
            Add($"{key}.samplerate", StringResource.SampleRate, FormatNumber(audio.SampleRate) + " Hz");
            Add($"{key}.channels", StringResource.Channels, FormatNumber(audio.ChannelCount));
            Add($"{key}.layout", StringResource.ChannelLayout, audio.ChannelLayout.ToString().ToLowerInvariant());
            Add($"{key}.sampleformat", StringResource.SampleFormat, audio.SampleFormat.ToString().ToLowerInvariant());
            if (audio.Bitrate > 0)
            {
                Add($"{key}.bitrate", StringResource.BitRate, FormatBitrate(audio.Bitrate));
            }

            Add($"{key}.decoder", StringResource.Decoder, chain.DecoderName ?? StringResource.None);
            Add($"{key}.decoder.mode", StringResource.DecodeMode, chain.IsHardwareAccelerated ? StringResource.Hardware : StringResource.Software);
            Add($"{key}.renderer", StringResource.Renderer, chain.RendererName ?? StringResource.None);
            Add($"{key}.clipped", StringResource.AudioClippedSamples, FormatNumber(chain.ClippedSamples));
        }

        ISubtitleChain[] subtitleChains = [.. info.SubtitleChains.Where(chain => chain.SelectedStream is not null)];
        for (int index = 0; index < subtitleChains.Length; index++)
        {
            ISubtitleChain chain = subtitleChains[index];
            if (chain.SelectedStream is not { } subtitle)
            {
                continue;
            }

            string suffix = index == 0 ? string.Empty : $" #{index + 1}";
            string key = $"subtitle#{index + 1}";
            Group($"group.{key}", StringResource.Subtitle + suffix);
            Add($"{key}.codec", StringResource.Codec, FormatCodec(subtitle));
            Add($"{key}.type", StringResource.Type, subtitle.IsBitmap ? StringResource.SubtitleTypeBitmap : StringResource.SubtitleTypeText);
            Add($"{key}.forced", StringResource.SubtitleForced, subtitle.IsForced ? StringResource.Yes : StringResource.No);
            if (!string.IsNullOrEmpty(subtitle.Language))
            {
                Add($"{key}.language", StringResource.Language, subtitle.Language);
            }

            if (!string.IsNullOrEmpty(subtitle.Title))
            {
                Add($"{key}.title", StringResource.Title, subtitle.Title);
            }

            if (subtitle.Bitrate > 0)
            {
                Add($"{key}.bitrate", StringResource.BitRate, FormatBitrate(subtitle.Bitrate));
            }

            Add($"{key}.decoder", StringResource.Decoder, chain.DecoderName ?? StringResource.None);
            Add($"{key}.renderer", StringResource.Renderer, chain.RendererName ?? StringResource.None);
        }

        Group("group.playback", StringResource.Playback);
        Add("playback.state", StringResource.State, info.State.ToString() + (string.IsNullOrEmpty(info.Error) ? string.Empty : $" ({info.Error})"));
        Add("playback.position", StringResource.Position, FormatTime(info.Position));
        Add("playback.underruns", StringResource.AudioUnderruns, FormatNumber(info.AudioChains.Sum(chain => chain.Underruns)));

        Rows = rows;
    }

    public string BuildCopyText()
    {
        return string.Join(Environment.NewLine, Rows
            .Where(row => !row.IsGroup)
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => $"{row.Key}: {row.Value}"));
    }

    private static string FormatCodec(MediaStreamInfo stream)
    {
        return string.IsNullOrEmpty(stream.Profile) ? stream.CodecName ?? stream.CodecId : $"{stream.CodecName ?? stream.CodecId} {stream.Profile}";
    }

    private static string FormatTime(TimeSpan value)
    {
        return value >= TimeSpan.FromHours(1)
            ? value.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static string FormatBitrate(long bitsPerSecond)
    {
        return bitsPerSecond >= 1_000_000
            ? FormatNumber(bitsPerSecond / 1_000_000.0) + " Mbps"
            : FormatNumber(bitsPerSecond / 1000.0) + " kbps";
    }

    private static string FormatMilliseconds(double milliseconds)
    {
        return milliseconds.ToString("N2", CultureInfo.InvariantCulture) + " ms";
    }

    private static string FormatNumber(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("#,##0.###", CultureInfo.InvariantCulture);
    }
}
