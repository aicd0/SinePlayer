// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

/// <summary>
/// The FFmpeg entry points the engine uses. The set is deliberately small: every declaration here is
/// a piece of the ABI the isolation boundary has to keep stable.
/// </summary>
internal static partial class FFmpegNative
{
    internal const string AvUtil = "avutil-61";
    internal const string AvCodec = "avcodec-63";
    internal const string AvFormat = "avformat-63";
    internal const string SwResample = "swresample-7";
    internal const string SwScale = "swscale-10";

    //
    // libavutil
    //

    [LibraryImport(AvUtil)]
    internal static partial uint avutil_version();

    [LibraryImport(AvUtil)]
    internal static partial nint av_version_info();

    [LibraryImport(AvUtil)]
    internal static partial nint av_get_pix_fmt_name(int format);

    [LibraryImport(AvUtil)]
    internal static partial nint av_get_sample_fmt_name(int format);

    [LibraryImport(AvUtil)]
    internal static partial int av_get_bytes_per_sample(int format);

    [LibraryImport(AvUtil)]
    internal static partial int av_sample_fmt_is_planar(int format);

    [LibraryImport(AvUtil)]
    internal static partial int av_channel_layout_describe(nint channelLayout, nint buffer, nuint bufferSize);

    [LibraryImport(AvUtil)]
    internal static partial int av_strerror(int error, nint buffer, nuint bufferSize);

    //
    // libavcodec
    //

    [LibraryImport(AvCodec)]
    internal static partial uint avcodec_version();

    [LibraryImport(AvCodec)]
    internal static partial nint avcodec_get_name(int codecId);

    [LibraryImport(AvCodec, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint avcodec_find_decoder_by_name(string name);

    [LibraryImport(AvCodec)]
    internal static partial nint avcodec_alloc_context3(nint codec);

    [LibraryImport(AvCodec)]
    internal static partial void avcodec_free_context(ref nint context);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_parameters_to_context(nint context, nint parameters);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_open2(nint context, nint codec, nint options);

    [LibraryImport(AvUtil, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int av_opt_set_int(nint obj, string name, long value, int searchFlags);

    [LibraryImport(AvUtil, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int av_opt_get_int(nint obj, string name, int searchFlags, out long value);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_send_packet(nint context, nint packet);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_receive_frame(nint context, nint frame);

    [LibraryImport(AvCodec)]
    internal static partial void avcodec_flush_buffers(nint context);

    [LibraryImport(AvCodec)]
    internal static partial nint avcodec_profile_name(int codecId, int profile);

    [LibraryImport(AvCodec)]
    internal static partial nint av_packet_alloc();

    [LibraryImport(AvCodec)]
    internal static partial void av_packet_free(ref nint packet);

    [LibraryImport(AvCodec)]
    internal static partial int av_packet_ref(nint destination, nint source);

    [LibraryImport(AvCodec)]
    internal static partial void av_packet_unref(nint packet);

    //
    // libavformat
    //

    [LibraryImport(AvFormat)]
    internal static partial uint avformat_version();

    [LibraryImport(AvFormat)]
    internal static partial int avformat_network_init();

    [LibraryImport(AvFormat, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int avformat_open_input(out nint context, string url, nint inputFormat, nint options);

    [LibraryImport(AvFormat)]
    internal static partial int avformat_find_stream_info(nint context, nint options);

    [LibraryImport(AvFormat)]
    internal static partial void avformat_close_input(ref nint context);

    [LibraryImport(AvFormat)]
    internal static partial int av_read_frame(nint context, nint packet);

    [LibraryImport(AvFormat)]
    internal static partial int av_seek_frame(nint context, int streamIndex, long timestamp, int flags);

    //
    // libavutil, frames
    //

    [LibraryImport(AvUtil)]
    internal static partial nint av_frame_alloc();

    [LibraryImport(AvUtil)]
    internal static partial void av_frame_free(ref nint frame);

    [LibraryImport(AvUtil)]
    internal static partial void av_frame_unref(nint frame);

    [LibraryImport(AvUtil)]
    internal static partial void av_frame_move_ref(nint destination, nint source);

    //
    // libavutil, dictionary access
    //

    [LibraryImport(AvUtil, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint av_dict_get(nint dictionary, string key, nint previous, int flags);
}
