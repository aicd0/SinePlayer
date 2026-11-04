// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.Graphics.Canvas.Text;

using SinePlayer.Core.Common.DebugTools;

namespace SinePlayer.Core.Playback.Backends.Win2D;

/// <summary>
/// The families the Win2D subtitle renderer can be asked to draw with. Read once and kept: enumerating the installed
/// fonts is slow enough to be felt when a settings page opens, and what is installed does not change meanwhile.
/// </summary>
public static class SubtitleFonts
{
    private const string TAG = nameof(SubtitleFonts);

    private static readonly Lock sLock = new();
    private static IReadOnlyList<string>? sFamilies;

    /// <summary>
    /// Every installed family, alphabetically. Empty when they cannot be read, which is something a picker has to survive
    /// rather than a failure worth raising: a renderer that is told no family falls back to its own.
    /// </summary>
    public static IReadOnlyList<string> GetFamilies()
    {
        lock (sLock)
        {
            return sFamilies ??= ReadFamilies();
        }
    }

    private static IReadOnlyList<string> ReadFamilies()
    {
        try
        {
            string[] families = CanvasTextFormat.GetSystemFontFamilies();
            if (families.Length == 0)
            {
                return [];
            }

            return [.. families.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex)
        {
            Logger.E(TAG, "Could not read the installed fonts", ex);
            return [];
        }
    }
}
