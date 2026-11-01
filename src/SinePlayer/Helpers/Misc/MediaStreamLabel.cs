// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.IO;
using System.Text;

using SinePlayer.Common.Localization;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Helpers.Misc;

internal static class MediaStreamLabel
{
    public static string Describe(MediaStreamInfo stream, MediaSource? source)
    {
        StringBuilder sb = new();

        sb.Append('#').Append(stream.Index);

        if (!string.IsNullOrWhiteSpace(stream.Language))
        {
            sb.Append(" [").Append(stream.Language).Append(']');
        }

        string? title = stream.Title;
        if (string.IsNullOrWhiteSpace(title) && source is not null)
        {
            title = Path.GetFileName(source.Locator.Value);
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append(' ').Append(title);
        }

        if (stream.IsDefault)
        {
            sb.Append(" <").Append(StringResourceProvider.Instance.Default).Append('>');
        }

        sb.Append(" - ").Append(stream.CodecName ?? stream.CodecId);

        string contents = stream switch
        {
            VideoStreamInfo video => $" {video.Width}x{video.Height} {video.FrameRate:0.###} fps",
            AudioStreamInfo audio => $" {audio.SampleRate} Hz {audio.ChannelCount} ch",
            SubtitleStreamInfo subtitle => string.Empty,
            _ => string.Empty,
        };

        sb.Append(contents);
        return sb.ToString();
    }
}
