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

        foreach (VideoStreamInfo video in media.VideoStreams)
        {
            Group("group.video", StringResource.Video);
            Add("video.codec", StringResource.Codec, FormatCodec(video));
            Add("video.resolution", StringResource.Resolution, video.Width + " x " + video.Height);
            if (video.FrameRate > 0)
            {
                Add("video.framerate", StringResource.FrameRate, video.FrameRate.ToString("0.###", CultureInfo.InvariantCulture) + " fps");
            }

            Add("video.pixelformat", StringResource.PixelFormat, video.PixelFormat.ToString().ToLowerInvariant());
            if (video.BitDepth > 0)
            {
                Add("video.bitdepth", StringResource.BitDepth, video.BitDepth.ToString(CultureInfo.InvariantCulture));
            }

            if (video.Bitrate > 0)
            {
                Add("video.bitrate", StringResource.BitRate, FormatBitrate(video.Bitrate));
            }

            Add("video.decoder", StringResource.Decoder, info.VideoDecoderName ?? StringResource.None);
            Add("video.decoder.mode", StringResource.DecodeMode, info.IsVideoHardwareAccelerated ? StringResource.Hardware : StringResource.Software);
            Add("video.renderer", StringResource.Renderer, info.VideoRendererName ?? StringResource.None);
        }

        foreach (AudioStreamInfo audio in media.AudioStreams)
        {
            Group("group.audio", StringResource.Audio);
            Add("audio.codec", StringResource.Codec, FormatCodec(audio));
            Add("audio.samplerate", StringResource.SampleRate, audio.SampleRate.ToString(CultureInfo.InvariantCulture) + " Hz");
            Add("audio.channels", StringResource.Channels, audio.ChannelCount.ToString(CultureInfo.InvariantCulture));
            Add("audio.layout", StringResource.ChannelLayout, audio.ChannelLayout.ToString().ToLowerInvariant());
            Add("audio.sampleformat", StringResource.SampleFormat, audio.SampleFormat.ToString().ToLowerInvariant());
            if (audio.Bitrate > 0)
            {
                Add("audio.bitrate", StringResource.BitRate, FormatBitrate(audio.Bitrate));
            }

            Add("audio.decoder", StringResource.Decoder, info.AudioDecoderName ?? StringResource.None);
            Add("audio.decoder.mode", StringResource.DecodeMode, info.IsAudioHardwareAccelerated ? StringResource.Hardware : StringResource.Software);
            Add("audio.renderer", StringResource.Renderer, info.AudioRendererName ?? StringResource.None);
        }

        Group("group.playback", StringResource.Playback);
        Add("playback.state", StringResource.State, info.State.ToString() + (string.IsNullOrEmpty(info.Error) ? string.Empty : $" ({info.Error})"));
        Add("playback.position", StringResource.Position, FormatTime(info.Position));
        Add("playback.presented", StringResource.PresentedFrames, info.PresentedFrames.ToString(CultureInfo.InvariantCulture));
        Add("playback.dropped", StringResource.DroppedFrames, info.DroppedFrames.ToString(CultureInfo.InvariantCulture));

        Add("playback.lag", StringResource.PictureLag, FormatMilliseconds(info.PictureLag.TotalMilliseconds));
        Add("playback.decode.video", StringResource.VideoDecodeTime, FormatMilliseconds(info.AverageVideoDecodeMilliseconds));
        Add("playback.render.video", StringResource.VideoRenderTime, FormatMilliseconds(info.AverageVideoRenderMilliseconds));
        Add("playback.underruns", StringResource.AudioUnderruns, info.AudioUnderruns.ToString(CultureInfo.InvariantCulture));

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
            ? (bitsPerSecond / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Mbps"
            : (bitsPerSecond / 1000).ToString(CultureInfo.InvariantCulture) + " kbps";
    }

    private static string FormatMilliseconds(double milliseconds)
    {
        return milliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
    }
}
