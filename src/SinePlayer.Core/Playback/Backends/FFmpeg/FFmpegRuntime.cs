// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.AppEnvironment;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Verifies the FFmpeg native libraries this build was written against, and holds what FFmpeg says about itself.
/// <para>
/// Loading them, and resolving the names the interop layer declares, belongs to <see cref="NativeLibraryLoader"/>, which is
/// the one place that knows how native files are laid out. What is left here is the part only this backend can answer:
/// whether the loaded libraries are the ones the interop was written for, which fails loudly rather than producing
/// undefined behaviour later.
/// </para>
/// </summary>
public static class FFmpegRuntime
{
    private const int ExpectedAvUtilMajor = 61;
    private const int ExpectedAvCodecMajor = 63;
    private const int ExpectedAvFormatMajor = 63;

    private static readonly object sLock = new();

    private static bool sInitialized;
    private static string? sVersion;

    /// <summary>Directory the libraries were loaded from, or null before initialisation.</summary>
    public static string? LibraryDirectory => NativeLibraryLoader.FfmpegDirectory;

    /// <summary>Version string reported by FFmpeg itself, or null before initialisation.</summary>
    public static string? Version => sVersion;

    /// <summary>
    /// Initialises the runtime if it has not been initialised yet.
    /// </summary>
    public static void EnsureInitialized()
    {
        lock (sLock)
        {
            if (!sInitialized)
            {
                InitializeCore();
            }
        }
    }

    private static void InitializeCore()
    {
        OperationResult loaded = NativeLibraryLoader.EnsureFfmpeg();
        if (!loaded.IsSuccessful)
        {
            throw new InvalidOperationException(loaded.Message);
        }

        VerifyVersions();
        sInitialized = true;
    }

    private static void VerifyVersions()
    {
        int avUtilMajor = GetMajor(FFmpegNative.avutil_version());
        int avCodecMajor = GetMajor(FFmpegNative.avcodec_version());
        int avFormatMajor = GetMajor(FFmpegNative.avformat_version());

        if (avUtilMajor != ExpectedAvUtilMajor || avCodecMajor != ExpectedAvCodecMajor || avFormatMajor != ExpectedAvFormatMajor)
        {
            throw new InvalidOperationException(
                $"FFmpeg native libraries in '{LibraryDirectory}' do not match the interop layer. " +
                $"Expected libavutil {ExpectedAvUtilMajor}, libavcodec {ExpectedAvCodecMajor}, libavformat {ExpectedAvFormatMajor}; " +
                $"found libavutil {avUtilMajor}, libavcodec {avCodecMajor}, libavformat {avFormatMajor}.");
        }

        sVersion = FFmpegInterop.ToUtf8String(FFmpegNative.av_version_info());
    }

    private static int GetMajor(uint version)
    {
        return (int)(version >> 16);
    }
}
