// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

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

    internal static ColourInfo ToColourInfo(int primaries, int transfer, int range)
    {
        return new ColourInfo(
            primaries switch
            {
                1 => ColourPrimaries.Bt709,
                9 => ColourPrimaries.Bt2020,
                _ => ColourPrimaries.Unknown,
            },
            transfer switch
            {
                1 => ColourTransfer.Bt709,
                16 => ColourTransfer.Smpte2084,
                18 => ColourTransfer.Hlg,
                _ => ColourTransfer.Unknown,
            },
            range switch
            {
                1 => ColourRange.Limited,
                2 => ColourRange.Full,
                _ => ColourRange.Unknown,
            });
    }

    /// <summary>
    /// The orientation a display matrix asks for. The matrix is nine 16.16 fixed point numbers that map the stored
    /// picture onto the display, so what a player has to apply is the inverse of it; with a determinant of +1 or -1
    /// that inverse is the matrix's own quarter turn read backwards. FFmpeg counts a turn counter-clockwise while a
    /// rotation here counts clockwise quarters, which is the other sign; a negative determinant is a mirrored
    /// picture, and a mirror reverses the sense of a turn, so the two signs cancel out.
    /// <para>
    /// What else the matrix can carry is dropped: the two entries that move the picture and the scale are ignored, as
    /// FFmpeg's own <c>av_display_rotation_get</c> ignores them, and an angle that is not a quarter turn is rounded to
    /// the nearest one rather than shown as a skewed picture. The magnitude of the linear part is divided out by
    /// <c>atan2</c>, which is what leaves the turn of a scaled matrix readable.
    /// </para>
    /// </summary>
    internal static unsafe VideoOrientation ToOrientation(int* matrix)
    {
        // The linear part of the matrix as it is written: x' = a x + b y, y' = c x + d y.
        double a = matrix[0] / 65536.0;
        double b = matrix[1] / 65536.0;
        double c = matrix[3] / 65536.0;
        double d = matrix[4] / 65536.0;

        if (a == 0.0 && b == 0.0 && c == 0.0 && d == 0.0)
        {
            return VideoOrientation.Identity;
        }

        bool mirrored = ((a * d) - (b * c)) < 0.0;

        // The turn of that inverse, read off its first column. A mirror commutes a turn into the other direction,
        // which is why a mirrored matrix is read without the sign that reverses it.
        double inverseSine = mirrored ? c : -c;
        int quarterTurns = (int)Math.Round(Math.Atan2(inverseSine, d) * 2.0 / Math.PI);
        return new VideoOrientation(quarterTurns, mirrored);
    }

    /// <summary><c>AV_CHANNEL_ORDER_NATIVE</c>: the union holds the <c>AV_CH_LAYOUT_*</c> mask.</summary>
    private const int NativeChannelOrder = 1;

    private const ulong MaskMono = 0x4;                // FC alone
    private const ulong MaskStereo = 0x3;              // FL|FR
    private const ulong MaskSurround21 = 0xB;          // FL|FR|LFE
    private const ulong MaskQuad = 0x33;               // FL|FR|BL|BR
    private const ulong MaskSurround50 = 0x37;         // FL|FR|FC|BL|BR
    private const ulong MaskSurround51 = 0x3F;         // FL|FR|FC|LFE|BL|BR
    private const ulong MaskSurround51Side = 0x60F;    // FL|FR|FC|LFE|SL|SR
    private const ulong MaskSurround61 = 0x70F;        // FL|FR|FC|LFE|BL|BR|BC
    private const ulong MaskSurround61Back = 0x13F;    // FL|FR|FC|LFE|SL|SR|BC
    private const ulong MaskSurround71 = 0x63F;        // FL|FR|FC|LFE|BL|BR|SL|SR

    internal static ChannelLayout ToChannelLayout(AVChannelLayout channelLayout)
    {
        if (channelLayout.Order == NativeChannelOrder)
        {
            ChannelLayout named = (ulong)channelLayout.Layout switch
            {
                MaskMono => ChannelLayout.Mono,
                MaskStereo => ChannelLayout.Stereo,
                MaskSurround21 => ChannelLayout.Surround21,
                MaskQuad => ChannelLayout.Quad,
                MaskSurround50 => ChannelLayout.Surround50,
                MaskSurround51 => ChannelLayout.Surround51,
                MaskSurround51Side => ChannelLayout.Surround51,
                MaskSurround61 => ChannelLayout.Surround61,
                MaskSurround61Back => ChannelLayout.Surround61,
                MaskSurround71 => ChannelLayout.Surround71,
                _ => ChannelLayout.Unknown,
            };

            if (named != ChannelLayout.Unknown)
            {
                return named;
            }
        }

        return ByChannelCount(channelLayout.ChannelCount);
    }

    private static ChannelLayout ByChannelCount(int channelCount)
    {
        return channelCount switch
        {
            1 => ChannelLayout.Mono,
            2 => ChannelLayout.Stereo,
            3 => ChannelLayout.Surround21,
            4 => ChannelLayout.Quad,
            5 => ChannelLayout.Surround50,
            6 => ChannelLayout.Surround51,
            7 => ChannelLayout.Surround61,
            8 => ChannelLayout.Surround71,
            _ => ChannelLayout.Unknown,
        };
    }

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

    internal static double ToRate(AVRational value)
    {
        if (value.Denominator == 0)
        {
            return 0;
        }

        return value.Numerator / (double)value.Denominator;
    }

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
