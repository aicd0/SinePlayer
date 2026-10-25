// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Globalization;
using System.Linq;

using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Data.Models.Media;

namespace SinePlayer.Common.Playback.Core;

internal static class TrackSelection
{
    private const string OffValue = "-1";

    internal readonly record struct Preference(bool IsSet, MediaStreamInfo? Stream);

    public static Preference Load(MediaModel media, MediaStreamKind kind, MediaInfo? info)
    {
        string? value = media.GetExt(KeyOf(kind));
        if (string.IsNullOrEmpty(value))
        {
            return default;
        }

        if (value == OffValue)
        {
            return new Preference(true, null);
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            return default;
        }

        MediaStreamInfo? stream = info?.Streams.FirstOrDefault(candidate => candidate.Index == index && candidate.Kind == kind);
        return stream is null ? default : new Preference(true, stream);
    }

    public static void Save(MediaModel media, MediaStreamKind kind, MediaStreamInfo? stream)
    {
        string value = stream is null ? OffValue : stream.Index.ToString(CultureInfo.InvariantCulture);
        if (media.GetExt(KeyOf(kind)) == value)
        {
            return;
        }

        media.SetExt(KeyOf(kind), value);
        CoroutineUtils.Run(media.FlushExt);
    }

    private static string KeyOf(MediaStreamKind kind)
    {
        return kind switch
        {
            MediaStreamKind.Video => MediaExt.SELECTED_VIDEO_TRACK,
            MediaStreamKind.Audio => MediaExt.SELECTED_AUDIO_TRACK,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only the picture and the sound can be chosen."),
        };
    }
}
