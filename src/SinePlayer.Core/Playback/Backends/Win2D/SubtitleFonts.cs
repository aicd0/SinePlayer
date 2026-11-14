// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.Graphics.Canvas.Text;

using SinePlayer.Core.Common.DebugTools;

namespace SinePlayer.Core.Playback.Backends.Win2D;

public static class SubtitleFonts
{
    private const string TAG = nameof(SubtitleFonts);

    internal const string DefaultFontFamily = "Segoe UI";

    private static readonly int[] StandardWeights = [100, 200, 300, 400, 500, 600, 700, 800, 900];

    private static readonly Lock sLock = new();
    private static IReadOnlyList<string>? sFamilies;
    private static IReadOnlyDictionary<string, IReadOnlyList<int>>? sWeights;

    public static IReadOnlyList<string> GetFamilies()
    {
        lock (sLock)
        {
            return sFamilies ??= ReadFamilies();
        }
    }

    public static IReadOnlyList<int> GetWeights(string? family)
    {
        string target = string.IsNullOrEmpty(family) ? DefaultFontFamily : family;

        lock (sLock)
        {
            IReadOnlyDictionary<string, IReadOnlyList<int>> weights = sWeights ??= ReadWeights();
            if (weights.TryGetValue(target, out IReadOnlyList<int>? found) && found.Count > 0)
            {
                return found;
            }
        }

        Logger.E(TAG, $"No weights were found for '{target}'");
        return StandardWeights;
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

            return families.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception ex)
        {
            Logger.E(TAG, "Could not read the installed fonts", ex);
            return [];
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> ReadWeights()
    {
        Dictionary<string, IReadOnlyList<int>> weights = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            Dictionary<string, HashSet<int>> collected = new(StringComparer.OrdinalIgnoreCase);
            using var set = CanvasFontSet.GetSystemFontSet();
            foreach (CanvasFontFace face in set.Fonts)
            {
                int weight = face.Weight.Weight;
                if (weight <= 0)
                {
                    continue;
                }

                // A family answers to the name it has in each language it names itself in, and a picker hands back
                // whichever of them it showed.
                foreach (string name in face.FamilyNames.Values)
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    if (!collected.TryGetValue(name, out HashSet<int>? familyWeights))
                    {
                        familyWeights = [];
                        collected[name] = familyWeights;
                    }

                    familyWeights.Add(weight);
                }
            }

            foreach (KeyValuePair<string, HashSet<int>> pair in collected)
            {
                weights[pair.Key] = pair.Value.Order().ToArray();
            }
        }
        catch (Exception ex)
        {
            Logger.E(TAG, "Could not read the installed font weights", ex);
        }

        return weights;
    }
}
