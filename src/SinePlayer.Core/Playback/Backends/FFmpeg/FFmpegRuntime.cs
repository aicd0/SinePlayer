// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Locates, loads and verifies the FFmpeg native libraries. This is the only place that knows how the
/// binaries are laid out, which keeps packaging concerns out of the rest of the backend.
/// <para>
/// The libraries are preloaded in dependency order and resolved by full path, so Windows never has to
/// search for them. If any library is missing, or its major version does not match the one the interop
/// layer was written against, initialisation fails immediately and loudly rather than producing
/// undefined behaviour later.
/// </para>
/// </summary>
public static class FFmpegRuntime
{
    private const int ExpectedAvUtilMajor = 61;
    private const int ExpectedAvCodecMajor = 63;
    private const int ExpectedAvFormatMajor = 63;

    /// <summary>Load order matters: a library must be loaded before anything that links against it.</summary>
    private static readonly string[] sLibraryNames =
    [
        FFmpegNative.AvUtil,
        FFmpegNative.SwResample,
        FFmpegNative.SwScale,
        FFmpegNative.AvCodec,
        FFmpegNative.AvFormat,
    ];

    private static readonly object sLock = new();

    private static bool sInitialized;
    private static string? sLibraryDirectory;
    private static string? sVersion;

    /// <summary>Directory the libraries were loaded from, or null before initialisation.</summary>
    public static string? LibraryDirectory => sLibraryDirectory;

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
                InitializeCore(null);
            }
        }
    }

    /// <summary>
    /// Initialises the runtime from an explicit directory, replacing any earlier initialisation. Use
    /// this when the application ships the libraries somewhere other than the default location.
    /// </summary>
    public static void Initialize(string? libraryDirectory)
    {
        lock (sLock)
        {
            InitializeCore(libraryDirectory);
        }
    }

    private static void InitializeCore(string? libraryDirectory)
    {
        string directory = ResolveLibraryDirectory(libraryDirectory);

        List<string> missing = [];
        foreach (string name in sLibraryNames)
        {
            if (!File.Exists(Path.Combine(directory, name + ".dll")))
            {
                missing.Add(name + ".dll");
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg native libraries are missing from '{directory}': {string.Join(", ", missing)}. " +
                "Provide the pinned FFmpeg build for this architecture.");
        }

        foreach (string name in sLibraryNames)
        {
            NativeLibrary.Load(Path.Combine(directory, name + ".dll"));
        }

        sLibraryDirectory = directory;
        sInitialized = true;
        NativeLibrary.SetDllImportResolver(typeof(FFmpegRuntime).Assembly, ResolveLibrary);

        VerifyVersions();
    }

    private static void VerifyVersions()
    {
        int avUtilMajor = GetMajor(FFmpegNative.avutil_version());
        int avCodecMajor = GetMajor(FFmpegNative.avcodec_version());
        int avFormatMajor = GetMajor(FFmpegNative.avformat_version());

        if (avUtilMajor != ExpectedAvUtilMajor || avCodecMajor != ExpectedAvCodecMajor || avFormatMajor != ExpectedAvFormatMajor)
        {
            throw new InvalidOperationException(
                $"FFmpeg native libraries in '{sLibraryDirectory}' do not match the interop layer. " +
                $"Expected libavutil {ExpectedAvUtilMajor}, libavcodec {ExpectedAvCodecMajor}, libavformat {ExpectedAvFormatMajor}; " +
                $"found libavutil {avUtilMajor}, libavcodec {avCodecMajor}, libavformat {avFormatMajor}.");
        }

        sVersion = FFmpegInterop.ToUtf8String(FFmpegNative.av_version_info());
    }

    /// <summary>
    /// Resolves the default directory: the NuGet package layout first (used by tests and unpackaged
    /// runs), then the plain application directory (used once the binaries sit beside the app).
    /// </summary>
    private static string ResolveLibraryDirectory(string? explicitDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return explicitDirectory;
        }

        string packaged = Path.Combine(AppContext.BaseDirectory, "runtimes", GetRuntimeIdentifier(), "native");
        if (Directory.Exists(packaged))
        {
            return packaged;
        }

        return AppContext.BaseDirectory;
    }

    private static string GetRuntimeIdentifier()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string? directory = sLibraryDirectory;
        if (directory is not null && IsFFmpegLibrary(libraryName))
        {
            string path = Path.Combine(directory, libraryName + ".dll");
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out nint handle))
            {
                return handle;
            }
        }

        // Anything else keeps the default resolution behaviour.
        return nint.Zero;
    }

    private static bool IsFFmpegLibrary(string libraryName)
    {
        foreach (string name in sLibraryNames)
        {
            if (string.Equals(name, libraryName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int GetMajor(uint version)
    {
        return (int)(version >> 16);
    }
}
