// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Translates between FFmpeg's vocabulary and the engine's. Everything crossing the isolation
/// boundary passes through here, which is why this is the only file in the backend that both reads
/// FFmpeg types and knows about the neutral contracts.
/// <para>
/// Where FFmpeg can name a value itself - codec names, pixel formats, sample formats - the name is
/// used as the mapping key instead of a replicated enum. That keeps the mapping stable across FFmpeg
/// versions: an unknown name degrades to <c>Unknown</c> instead of silently meaning something else.
/// </para>
/// </summary>
internal static class FFmpegInterop
{
    private const int ErrorBufferSize = 256;

    internal static string? ToUtf8String(nint pointer)
    {
        return pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);
    }

    internal static string GetCodecId(int codecId)
    {
        return ToUtf8String(FFmpegNative.avcodec_get_name(codecId)) ?? "unknown";
    }

    internal static string? GetProfileName(int codecId, int profile)
    {
        return profile < 0 ? null : ToUtf8String(FFmpegNative.avcodec_profile_name(codecId, profile));
    }

    internal static MediaStreamKind ToStreamKind(int mediaType)
    {
        return mediaType switch
        {
            FFmpegConstants.MediaTypeVideo => MediaStreamKind.Video,
            FFmpegConstants.MediaTypeAudio => MediaStreamKind.Audio,
            FFmpegConstants.MediaTypeSubtitle => MediaStreamKind.Subtitle,
            _ => MediaStreamKind.Unknown,
        };
    }

    internal static PixelFormat ToPixelFormat(int format)
    {
        return GetPixelFormatName(format) switch
        {
            "yuv420p" => PixelFormat.Yuv420P,
            "yuv420p10le" => PixelFormat.Yuv420P10,
            "yuv422p" => PixelFormat.Yuv422P,
            "yuv444p" => PixelFormat.Yuv444P,
            "nv12" => PixelFormat.Nv12,
            "p010le" => PixelFormat.P010,
            "p016le" => PixelFormat.P016,
            "p012le" => PixelFormat.P016,
            "bgra" => PixelFormat.Bgra8,
            "rgba" => PixelFormat.Rgba8,
            _ => PixelFormat.Unknown,
        };
    }

    /// <summary>The name FFmpeg gives a pixel format, or null when the format is not one it knows.</summary>
    internal static string? GetPixelFormatName(int format)
    {
        return ToUtf8String(FFmpegNative.av_get_pix_fmt_name(format));
    }

    internal static AudioSampleFormat ToSampleFormat(int format)
    {
        string? name = ToUtf8String(FFmpegNative.av_get_sample_fmt_name(format));
        return name switch
        {
            "u8" or "u8p" => AudioSampleFormat.UInt8,
            "s16" or "s16p" => AudioSampleFormat.Int16,
            "s32" or "s32p" => AudioSampleFormat.Int32,
            "flt" or "fltp" => AudioSampleFormat.Float32,
            "dbl" or "dblp" => AudioSampleFormat.Float64,
            _ => AudioSampleFormat.Unknown,
        };
    }

    /// <summary>
    /// Channel count is what matters to the pipeline, so the layout is derived from it and the
    /// descriptive name is only used for reporting.
    /// </summary>
    internal static ChannelLayout ToChannelLayout(int channelCount)
    {
        return channelCount switch
        {
            1 => ChannelLayout.Mono,
            2 => ChannelLayout.Stereo,
            3 => ChannelLayout.Surround21,
            4 => ChannelLayout.Quad,
            5 => ChannelLayout.Surround50,
            6 => ChannelLayout.Surround51,
            8 => ChannelLayout.Surround71,
            _ => ChannelLayout.Unknown,
        };
    }

    /// <summary>
    /// Planes a decoded frame uses. Semi-planar formats keep chroma in one plane, planar formats in
    /// one per component, and packed formats in a single plane.
    /// </summary>
    internal static int GetPlaneCount(PixelFormat format)
    {
        return format switch
        {
            PixelFormat.Nv12 or PixelFormat.P010 or PixelFormat.P016 => 2,
            PixelFormat.Yuv420P or PixelFormat.Yuv420P10 or PixelFormat.Yuv422P or PixelFormat.Yuv444P => 3,
            _ => 1,
        };
    }

    internal static string GetErrorMessage(int error)
    {
        unsafe
        {
            byte* buffer = stackalloc byte[ErrorBufferSize];
            if (FFmpegNative.av_strerror(error, (nint)buffer, ErrorBufferSize) < 0)
            {
                return $"FFmpeg error {error}";
            }

            return Marshal.PtrToStringUTF8((nint)buffer) ?? $"FFmpeg error {error}";
        }
    }

    /// <summary>Converts a value expressed in a stream's time base into a time span.</summary>
    internal static TimeSpan ToTimeSpan(long value, AVRational timeBase)
    {
        if (value == FFmpegConstants.NoPtsValue || timeBase.Denominator <= 0)
        {
            return TimeSpan.Zero;
        }

        double seconds = value * (double)timeBase.Numerator / timeBase.Denominator;

        // Pre-roll is real but nothing can be shown before the start, so negative values read as the
        // start. Everything else is taken at face value: an earlier guard clamped anything past a
        // second to zero, which stamped every frame after the first second of a file as time zero and
        // made the synchronizer drop all of them.
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }

    /// <summary>Converts a rational rate, for example a frame rate, into a number.</summary>
    internal static double ToRate(AVRational value)
    {
        if (value.Denominator == 0)
        {
            return 0;
        }

        return value.Numerator / (double)value.Denominator;
    }

    /// <summary>
    /// Reads a length written as <c>HH:MM:SS.nnnnnnnnn</c>, which is how Matroska spells a track's own length in its
    /// <c>DURATION</c> tag — the field a demuxer leaves empty for that container. Anything else is no answer at all,
    /// because a length that cannot be read is not a length.
    /// </summary>
    internal static TimeSpan? ParseDurationTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value.Split(':');
        if (parts.Length != 3)
        {
            return null;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
        {
            return null;
        }

        string[] seconds = parts[2].Split('.');
        if (!int.TryParse(seconds[0], NumberStyles.None, CultureInfo.InvariantCulture, out int whole))
        {
            return null;
        }

        // A TimeSpan counts seven digits of a second and the tag writes nine: the rest is below anything this
        // pipeline can schedule, so it is cut rather than rounded.
        int fraction = 0;
        if (seconds.Length > 1)
        {
            string digits = seconds[1];
            digits = digits.Length > 7 ? digits[..7] : digits.PadRight(7, '0');
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out fraction))
            {
                return null;
            }
        }

        long ticks = ((hours * 3600L + minutes * 60L + whole) * TimeSpan.TicksPerSecond) + fraction;
        return TimeSpan.FromTicks(ticks);
    }

    internal static string? GetMetadata(nint dictionary, string key)
    {
        if (dictionary == 0)
        {
            return null;
        }

        nint entry = FFmpegNative.av_dict_get(dictionary, key, 0, 0);
        if (entry == 0)
        {
            return null;
        }

        unsafe
        {
            return ToUtf8String(((AVDictionaryEntry*)entry)->Value);
        }
    }
}
