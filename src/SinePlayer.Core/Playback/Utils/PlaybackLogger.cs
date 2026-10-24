// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.DebugTools;

namespace SinePlayer.Core.Playback.Utils;

internal static class PlaybackLogger
{
    private const string TAG = nameof(PlaybackLogger);

    public static void D(string? tag, string? message)
    {
        Logger.D(Compose(tag), message);
    }

    public static void I(string? tag, string? message)
    {
        Logger.I(Compose(tag), message);
    }

    public static void W(string? tag, string? message)
    {
        Logger.W(Compose(tag), message);
    }

    public static void E(string? tag, string? message)
    {
        Logger.E(Compose(tag), message);
    }

    public static void E(string? tag, string? message, Exception? exception)
    {
        Logger.E(Compose(tag), message, exception);
    }

    public static void E(string? tag, Exception? exception)
    {
        Logger.E(Compose(tag), exception);
    }

    public static void F(string? tag, string? message)
    {
        Logger.F(Compose(tag), message);
    }

    public static void F(string? tag, string? message, Exception? exception)
    {
        Logger.F(Compose(tag), message, exception);
    }

    private static string Compose(string? tag)
    {
        return $"{TAG}/{tag}";
    }
}
