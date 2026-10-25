// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Text;

using SinePlayer.Common.Localization;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Helpers.Misc;

internal static class MediaStreamLabel
{
    public static string Describe(MediaStreamInfo stream)
    {
        StringBuilder sb = new();

        sb.Append('#').Append(stream.Index);

        if (!string.IsNullOrWhiteSpace(stream.Language))
        {
            sb.Append(" [").Append(stream.Language).Append(']');
        }

        if (!string.IsNullOrWhiteSpace(stream.Title))
        {
            sb.Append(' ').Append(stream.Title);
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
            _ => string.Empty,
        };

        sb.Append(contents);
        return sb.ToString();
    }
}
