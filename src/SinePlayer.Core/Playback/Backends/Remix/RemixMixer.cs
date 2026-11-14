// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;

namespace SinePlayer.Core.Playback.Backends.Remix;

public static class RemixMixer
{
    public const int UnityLevel = 100;
    public const int MaximumLevel = 200;

    private const int MaximumTargetChannelCount = 2;

    private static readonly GroupSpec Mono = new("mono", "M", UnityLevel);
    private static readonly GroupSpec LeftRight = new("stereo", "L/R", UnityLevel);
    private static readonly GroupSpec Front = new("front", "FL/FR", UnityLevel);
    private static readonly GroupSpec Center = new("center", "FC", 71);
    private static readonly GroupSpec LowFrequency = new("lfe", "LFE", 0);
    private static readonly GroupSpec Side = new("side", "SL/SR", 71);
    private static readonly GroupSpec Back = new("back", "BL/BR", 71);
    private static readonly GroupSpec BackCenter = new("backCenter", "BC", 71);
    private static readonly GroupSpec FrontOfCenter = new("frontOfCenter", "FLC/FRC", 71);
    private static readonly GroupSpec TopFront = new("topFront", "TFL/TFR", 71);

    private static readonly LayoutSpec[] Layouts =
    [
        new(ChannelLayout.Mono, 1, [new(Mono, [0], [0])]),
        new(ChannelLayout.Stereo, 2, [new(LeftRight, [0], [1])]),
        new(ChannelLayout.Surround21, 3, [new(Front, [0], [1]), new(LowFrequency, [2], [2])]),
        new(ChannelLayout.Quad, 4, [new(Front, [0], [1]), new(Back, [2], [3])]),
        new(ChannelLayout.Surround50, 5, [new(Front, [0], [1]), new(Center, [2], [2]), new(Back, [3], [4])]),
        new(ChannelLayout.Surround51, 6, [new(Front, [0], [1]), new(Center, [2], [2]), new(LowFrequency, [3], [3]), new(Back, [4], [5])]),
        new(ChannelLayout.Surround51Side, 6, [new(Front, [0], [1]), new(Center, [2], [2]), new(LowFrequency, [3], [3]), new(Side, [4], [5])]),
        new(ChannelLayout.Surround61, 7, [new(Front, [0], [1]), new(Center, [2], [2]), new(LowFrequency, [3], [3]), new(BackCenter, [4], [4]), new(Side, [5], [6])]),
        new(ChannelLayout.Surround61Back, 7, [new(Front, [0], [1]), new(Center, [2], [2]), new(LowFrequency, [3], [3]), new(Back, [4], [5]), new(BackCenter, [6], [6])]),
        new(ChannelLayout.Surround61Front, 7, [new(Front, [0], [1]), new(LowFrequency, [2], [2]), new(FrontOfCenter, [3], [4]), new(Side, [5], [6])]),
        new(ChannelLayout.Surround70, 7, [new(Front, [0], [1]), new(Center, [2], [2]), new(Back, [3], [4]), new(Side, [5], [6])]),
        new(ChannelLayout.Surround70Front, 7, [new(Front, [0], [1]), new(Center, [2], [2]), new(FrontOfCenter, [3], [4]), new(Side, [5], [6])]),
        new(ChannelLayout.Surround71, 8, [new(Front, [0], [1]), new(Center, [2], [2]), new(LowFrequency, [3], [3]), new(Side, [6], [7]), new(Back, [4], [5])]),
        new(ChannelLayout.Surround71Wide, 8, [new(Front, [0], [1]), new(Center, [2], [2]), new(LowFrequency, [3], [3]), new(Back, [4], [5]), new(FrontOfCenter, [6], [7])]),
        new(ChannelLayout.Surround71WideSide, 8, [new(Front, [0], [1]), new(Center, [2], [2]), new(LowFrequency, [3], [3]), new(FrontOfCenter, [4], [5]), new(Side, [6], [7])]),
        new(ChannelLayout.Surround512, 8, [new(Front, [0], [1]), new(Center, [2], [2]), new(LowFrequency, [3], [3]), new(Side, [4], [5]), new(TopFront, [6], [7])]),
    ];

    public static IReadOnlyList<RemixChannelGroup> Describe(ChannelLayout layout, RemixLevelSet set)
    {
        LayoutSpec? spec = SpecOf(layout);
        if (spec is null)
        {
            return new[] { Group(LeftRight, set) };
        }

        List<RemixChannelGroup> groups = [];
        foreach (GroupRoute route in spec.Routes)
        {
            groups.Add(Group(route.Group, set));
        }

        return groups;
    }

    /// <summary>The gain each channel of a layout carries, so a stream that keeps its layout can be trimmed speaker by speaker.</summary>
    internal static bool TryCreateRemix(ChannelLayout layout, int channelCount, IReadOnlyDictionary<string, int>? levels, out float[] gains)
    {
        gains = [];

        LayoutSpec? spec = SpecOf(layout);
        if (spec is null || spec.ChannelCount != channelCount)
        {
            return false;
        }

        gains = new float[channelCount];
        bool unity = true;
        foreach (GroupRoute route in spec.Routes)
        {
            float level = LevelOf(route.Group, RemixLevelSet.Remix, levels) / 100.0f;
            unity = unity && level == 1.0f;
            foreach (int channel in route.Left)
            {
                gains[channel] = level;
            }

            foreach (int channel in route.Right)
            {
                gains[channel] = level;
            }
        }

        if (unity)
        {
            gains = [];
            return false;
        }

        return true;
    }

    /// <summary>
    /// The coefficients that fold a layout down to one or two channels, or false when the layout is one this table has
    /// no groups for, when the channel count disagrees with it, or when it is not wider than the target.
    /// </summary>
    internal static bool TryCreateDownmix(ChannelLayout layout, int channelCount, int targetChannelCount, IReadOnlyDictionary<string, int>? levels, out FoldMatrix matrix)
    {
        matrix = default;

        LayoutSpec? spec = SpecOf(layout);
        if (spec is null || spec.ChannelCount != channelCount || channelCount <= targetChannelCount || targetChannelCount < 1 || targetChannelCount > MaximumTargetChannelCount)
        {
            return false;
        }

        float[] left = new float[channelCount];
        float[] right = new float[channelCount];
        foreach (GroupRoute route in spec.Routes)
        {
            float level = LevelOf(route.Group, RemixLevelSet.Downmix, levels) / 100.0f;
            foreach (int channel in route.Left)
            {
                left[channel] = level;
            }

            foreach (int channel in route.Right)
            {
                right[channel] = level;
            }
        }

        if (targetChannelCount == 1)
        {
            // Mono is the stereo fold with its two sides averaged, so a signal that is in both of them keeps its level.
            for (int channel = 0; channel < channelCount; channel++)
            {
                left[channel] = (left[channel] + right[channel]) * 0.5f;
            }

            matrix = new FoldMatrix(left, [], 1);
            return true;
        }

        matrix = new FoldMatrix(left, right, targetChannelCount);
        return true;
    }

    private static LayoutSpec? SpecOf(ChannelLayout layout)
    {
        foreach (LayoutSpec spec in Layouts)
        {
            if (spec.Layout == layout)
            {
                return spec;
            }
        }

        return null;
    }

    private static RemixChannelGroup Group(GroupSpec group, RemixLevelSet set)
    {
        return new RemixChannelGroup(group.Key, group.Label, LevelOf(group, set, null));
    }

    /// <summary>
    /// The level a group carries: the one the listener chose, clamped to the range a level may take, or the default of
    /// the set in charge when nothing was chosen.
    /// </summary>
    private static int LevelOf(GroupSpec group, RemixLevelSet set, IReadOnlyDictionary<string, int>? levels)
    {
        if (levels is not null && levels.TryGetValue(group.Key, out int level))
        {
            return Math.Clamp(level, 0, MaximumLevel);
        }

        return set == RemixLevelSet.Remix ? UnityLevel : group.DownmixDefaultLevel;
    }

    /// <summary>One group of an arrangement and the channels of it that reach each side of the fold.</summary>
    private readonly record struct GroupRoute(GroupSpec Group, int[] Left, int[] Right);

    /// <summary>The coefficients that take one block of a layout to the channels it is folded to.</summary>
    internal readonly record struct FoldMatrix(float[] Left, float[] Right, int ChannelCount);

    /// <summary>A group of speakers one slider covers, with the level the downmix set gives it.</summary>
    private sealed record GroupSpec(string Key, string Label, int DownmixDefaultLevel);

    /// <summary>The groups of an arrangement, in the order a mixing UI lists them.</summary>
    private sealed record LayoutSpec(ChannelLayout Layout, int ChannelCount, GroupRoute[] Routes);
}