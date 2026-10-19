// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Playback;
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
    public int StructureVersion => _structureVersion;
    public IReadOnlyList<PlaybackInfoRow> Rows { get; private set; } = [];

    private int _structureVersion;
    private readonly List<string> _keys = [];

    public void Update(IPlaybackInfoProvider? info)
    {
        if (info?.Media is not { } media)
        {
            IsEmpty = true;
            Apply([], []);
            return;
        }

        IsEmpty = false;

        List<PlaybackInfoRow> rows = [];
        List<string> keys = [];
        void Add(string key, string name, string value)
        {
            keys.Add(key);
            rows.Add(PlaybackInfoRow.Property(key, name, value));
        }

        void Group(string key, string name)
        {
            keys.Add(key);
            rows.Add(PlaybackInfoRow.Group(key, name));
        }

        Group("group.media", "Media");
        Add("media.container", "Container", media.ContainerId);
        Add("media.length", "Length", FormatTime(media.Duration));
        if (media.Bitrate > 0)
        {
            Add("media.bitrate", "Bit rate", FormatBitrate(media.Bitrate));
        }

        foreach (VideoStreamInfo video in media.VideoStreams)
        {
            Group("group.video", "Video");
            Add("video.codec", "Codec", FormatCodec(video));
            Add("video.resolution", "Resolution", video.Width + "×" + video.Height);
            if (video.FrameRate > 0)
            {
                Add("video.framerate", "Frame rate", video.FrameRate.ToString("0.###", CultureInfo.InvariantCulture) + " fps");
            }

            Add("video.pixelformat", "Pixel format", video.PixelFormat.ToString().ToLowerInvariant());
            if (video.BitDepth > 0)
            {
                Add("video.bitdepth", "Bit depth", video.BitDepth.ToString(CultureInfo.InvariantCulture));
            }

            if (video.RotationDegrees != 0)
            {
                Add("video.rotation", "Rotation", video.RotationDegrees.ToString(CultureInfo.InvariantCulture) + "°");
            }

            if (video.Bitrate > 0)
            {
                Add("video.bitrate", "Bit rate", FormatBitrate(video.Bitrate));
            }

            Add("video.decoder", "Decoder", info.IsVideoHardwareAccelerated ? "Hardware" : "Software");
        }

        foreach (AudioStreamInfo audio in media.AudioStreams)
        {
            Group("group.audio", "Audio");
            Add("audio.codec", "Codec", FormatCodec(audio));
            Add("audio.samplerate", "Sample rate", audio.SampleRate.ToString(CultureInfo.InvariantCulture) + " Hz");
            Add("audio.channels", "Channels", audio.ChannelCount.ToString(CultureInfo.InvariantCulture));
            Add("audio.layout", "Channel layout", audio.ChannelLayout.ToString().ToLowerInvariant());
            Add("audio.sampleformat", "Sample format", audio.SampleFormat.ToString().ToLowerInvariant());
            if (audio.Bitrate > 0)
            {
                Add("audio.bitrate", "Bit rate", FormatBitrate(audio.Bitrate));
            }

            Add("audio.decoder", "Decoder", info.IsAudioHardwareAccelerated ? "Hardware" : "Software");
        }

        Group("group.playback", "Playback");
        Add("playback.state", "State", info.State.ToString() + (string.IsNullOrEmpty(info.Error) ? string.Empty : $" ({info.Error})"));
        Add("playback.position", "Position", FormatTime(info.Position));
        Add("playback.presented", "Presented frames", info.PresentedFrames.ToString(CultureInfo.InvariantCulture));
        Add("playback.dropped", "Dropped frames", info.DroppedFrames.ToString(CultureInfo.InvariantCulture));
        if (info.DisplayedFrames is long displayed)
        {
            Add("playback.displayed", "Displayed frames", displayed.ToString(CultureInfo.InvariantCulture));
        }

        Add("playback.lag", "Picture lag", FormatMilliseconds(info.PictureLag.TotalMilliseconds));
        Add("playback.decode", "Decode time", FormatMilliseconds(info.AverageDecodeMilliseconds));
        Add("playback.render", "Render time", FormatMilliseconds(info.AverageRenderMilliseconds));
        if (!string.IsNullOrEmpty(info.StarvedFor))
        {
            Add("playback.starved", "Waiting for", info.StarvedFor == "video" ? "Video" : "Audio");
        }

        Add("playback.volume", "Volume", Math.Round(info.Volume * 100.0).ToString(CultureInfo.InvariantCulture) + "%");
        Add("playback.muted", "Muted", info.IsMuted ? "Yes" : "No");

        Apply(rows, keys);
    }

    public string BuildCopyText()
    {
        return string.Join(Environment.NewLine, Rows
            .Where(row => !row.IsGroup)
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => $"{row.Key}: {row.Value}"));
    }

    private void Apply(List<PlaybackInfoRow> rows, List<string> keys)
    {
        if (!SameKeys(keys))
        {
            _keys.Clear();
            _keys.AddRange(keys);
            _structureVersion++;
        }

        Rows = rows;
    }

    private bool SameKeys(List<string> keys)
    {
        if (keys.Count != _keys.Count)
        {
            return false;
        }

        for (int index = 0; index < keys.Count; index++)
        {
            if (keys[index] != _keys[index])
            {
                return false;
            }
        }

        return true;
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
