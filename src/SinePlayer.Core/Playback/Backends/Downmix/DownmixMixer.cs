// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;

namespace SinePlayer.Core.Playback.Backends.Downmix;

/// <summary>
/// What each speaker arrangement is made of: which speakers form one group, the order a mixing UI lists those
/// groups in, what each group is called, and the level it carries until the listener changes it.
/// <para>
/// This list is the only place those facts are written down. The fold is built from it and a mixing UI reads it
/// through <see cref="Describe"/>, so a group cannot exist in one of them and be missing from the other.
/// </para>
/// </summary>
public static class DownmixMixer
{
    private const int TargetChannelCount = 2;
    private const int UnityLevel = 100;
    private const int MaximumLevel = 200;

    private static readonly GroupSpec Mono = new("mono", "M", 100);
    private static readonly GroupSpec LeftRight = new("stereo", "L/R", 100);
    private static readonly GroupSpec Front = new("front", "FL/FR", 100);
    private static readonly GroupSpec Center = new("center", "FC", 71);
    private static readonly GroupSpec LowFrequency = new("lfe", "LFE", 0);
    private static readonly GroupSpec Side = new("side", "SL/SR", 71);
    private static readonly GroupSpec Back = new("back", "BL/BR", 71);
    private static readonly GroupSpec BackCenter = new("backCenter", "BC", 71);
    private static readonly GroupSpec FrontOfCenter = new("frontOfCenter", "FLC/FRC", 71);
    private static readonly GroupSpec TopFront = new("topFront", "TFL/TFR", 71);

    /// <summary>
    /// Each arrangement's groups in the order a mixing UI lists them, with the channels each group feeds. The channel
    /// order is the one the layout's mask asserts, which is the order the decoder emits.
    /// </summary>
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

    /// <summary>
    /// The groups of a layout in the order a mixing UI lists them, or the stereo pair for an arrangement whose
    /// speakers are not known here: a section with no rows would tell the listener nothing at all.
    /// </summary>
    public static IReadOnlyList<DownmixChannelGroup> Describe(ChannelLayout layout)
    {
        LayoutSpec? spec = SpecOf(layout);
        if (spec is null)
        {
            return new[] { Group(LeftRight) };
        }

        List<DownmixChannelGroup> groups = [];
        foreach (GroupRoute route in spec.Routes)
        {
            groups.Add(Group(route.Group));
        }

        return groups;
    }

    /// <summary>
    /// The coefficients that fold a layout down to two channels, or false when the layout is one this table has no
    /// groups for, when the channel count disagrees with it, or when the stream is already two channels wide.
    /// </summary>
    internal static bool TryCreateFold(ChannelLayout layout, int channelCount, IReadOnlyDictionary<string, int>? levels, out FoldMatrix matrix)
    {
        matrix = default;

        LayoutSpec? spec = SpecOf(layout);
        if (spec is null || spec.ChannelCount != channelCount || channelCount <= TargetChannelCount)
        {
            return false;
        }

        matrix = Build(spec, TargetChannelCount, levels);
        return true;
    }

    /// <summary>
    /// The matrix of an arrangement that is already one or two channels wide: the listener's level for its pair, or
    /// false when that level is unchanged, so a block with nothing to do is handed on as it arrived.
    /// </summary>
    internal static bool TryCreateIdentity(ChannelLayout layout, int channelCount, IReadOnlyDictionary<string, int>? levels, out FoldMatrix matrix)
    {
        matrix = default;

        LayoutSpec? spec = SpecOf(layout);
        if (spec is null || spec.ChannelCount != channelCount || channelCount > TargetChannelCount)
        {
            return false;
        }

        if (LevelOf(spec.Routes[0].Group, levels) == UnityLevel)
        {
            return false;
        }

        matrix = Build(spec, spec.ChannelCount, levels);
        return true;
    }

    private static FoldMatrix Build(LayoutSpec spec, int outputChannelCount, IReadOnlyDictionary<string, int>? levels)
    {
        float[] left = new float[spec.ChannelCount];
        float[] right = new float[spec.ChannelCount];
        foreach (GroupRoute route in spec.Routes)
        {
            float level = LevelOf(route.Group, levels) / 100.0f;
            foreach (int channel in route.Left)
            {
                left[channel] = level;
            }

            foreach (int channel in route.Right)
            {
                right[channel] = level;
            }
        }

        return new FoldMatrix(left, right, outputChannelCount);
    }

    /// <summary>
    /// The level a group carries: the one the listener chose, clamped to the range a level may take, or the default
    /// this table gives it when nothing was chosen.
    /// </summary>
    private static int LevelOf(GroupSpec group, IReadOnlyDictionary<string, int>? levels)
    {
        if (levels is not null && levels.TryGetValue(group.Key, out int level))
        {
            return Math.Clamp(level, 0, MaximumLevel);
        }

        return group.DefaultLevel;
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

    private static DownmixChannelGroup Group(GroupSpec group)
    {
        return new DownmixChannelGroup(group.Key, group.Label, group.DefaultLevel);
    }

    /// <summary>One group of an arrangement and the channels of it that reach each side of the fold.</summary>
    private readonly record struct GroupRoute(GroupSpec Group, int[] Left, int[] Right);

    /// <summary>The coefficients that take one block of a layout to the channels it is folded to.</summary>
    internal readonly record struct FoldMatrix(float[] Left, float[] Right, int ChannelCount);

    /// <summary>A group of speakers one slider covers, and the level it carries until it is changed.</summary>
    private sealed record GroupSpec(string Key, string Label, int DefaultLevel);

    /// <summary>The groups of an arrangement, in the order a mixing UI lists them.</summary>
    private sealed record LayoutSpec(ChannelLayout Layout, int ChannelCount, GroupRoute[] Routes);
}