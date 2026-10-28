// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

internal readonly record struct FFmpegDecoderCandidate(nint Codec, string Name);

internal static class FFmpegDecoders
{
    internal static IEnumerable<FFmpegDecoderCandidate> ByCodecIdThenNames(int codecId, params string[] names)
    {
        return Enumerate(codecId, names, codecIdFirst: true);
    }

    internal static IEnumerable<FFmpegDecoderCandidate> ByNamesThenCodecId(int codecId, params string[] names)
    {
        return Enumerate(codecId, names, codecIdFirst: false);
    }

    private static IEnumerable<FFmpegDecoderCandidate> Enumerate(int codecId, string[] names, bool codecIdFirst)
    {
        HashSet<nint> seen = [];
        string codecName = FFmpegInterop.GetCodecId(codecId);

        if (codecIdFirst && TryFind(codecId, codecName, seen) is { } byId)
        {
            yield return byId;
        }

        foreach (string name in names)
        {
            nint codec = FFmpegNative.avcodec_find_decoder_by_name(name);
            if (codec == 0 || !seen.Add(codec))
            {
                continue;
            }

            yield return new FFmpegDecoderCandidate(codec, name);
        }

        if (!codecIdFirst && TryFind(codecId, codecName, seen) is { } fallback)
        {
            yield return fallback;
        }
    }

    private static FFmpegDecoderCandidate? TryFind(int codecId, string name, HashSet<nint> seen)
    {
        nint codec = FFmpegNative.avcodec_find_decoder(codecId);
        return codec != 0 && seen.Add(codec) ? new FFmpegDecoderCandidate(codec, name) : null;
    }
}
