// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.DebugTools;

namespace SinePlayer.Core.Playback.Utils;

/// <summary>
/// Where everything playback says goes. A component logs here rather than through <see cref="Logger"/>, so that the
/// whole subsystem can be found under one tag — <c>PlaybackLogger/&lt;the part that said it&gt;</c> — and so that
/// what playback says can later be shaped (rate-limited, kept in memory, shown in a diagnostics pane) without every
/// call site moving.
/// <para>
/// For now it only passes the message on. The tag it passes has two segments: this class first, then the part that
/// produced the message, which keeps both the subsystem and the origin in every line without a caller having to
/// spell either out.
/// </para>
/// </summary>
internal static class PlaybackLogger
{
    private const string TAG = nameof(PlaybackLogger);

    /// <param name="tag">The part of playback that is speaking — its class name, as it has always been.</param>
    public static void D(string? tag, string? message)
    {
        Logger.D(Compose(tag), message);
    }

    /// <param name="tag">The part of playback that is speaking — its class name, as it has always been.</param>
    public static void I(string? tag, string? message)
    {
        Logger.I(Compose(tag), message);
    }

    /// <param name="tag">The part of playback that is speaking — its class name, as it has always been.</param>
    public static void W(string? tag, string? message)
    {
        Logger.W(Compose(tag), message);
    }

    /// <param name="tag">The part of playback that is speaking — its class name, as it has always been.</param>
    public static void E(string? tag, string? message)
    {
        Logger.E(Compose(tag), message);
    }

    /// <param name="tag">The part of playback that is speaking — its class name, as it has always been.</param>
    public static void E(string? tag, string? message, Exception? exception)
    {
        Logger.E(Compose(tag), message, exception);
    }

    /// <param name="tag">The part of playback that is speaking — its class name, as it has always been.</param>
    public static void E(string? tag, Exception? exception)
    {
        Logger.E(Compose(tag), exception);
    }

    /// <param name="tag">The part of playback that is speaking — its class name, as it has always been.</param>
    public static void F(string? tag, string? message)
    {
        Logger.F(Compose(tag), message);
    }

    /// <param name="tag">The part of playback that is speaking — its class name, as it has always been.</param>
    public static void F(string? tag, string? message, Exception? exception)
    {
        Logger.F(Compose(tag), message, exception);
    }

    private static string Compose(string? tag)
    {
        return $"{TAG}/{tag}";
    }
}
