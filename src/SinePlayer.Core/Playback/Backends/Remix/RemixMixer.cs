// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;

namespace SinePlayer.Core.Playback.Backends.Remix;

public static class RemixMixer
{
    /// <summary>The level a channel carries when nothing has been asked of it: full scale, as a gain.</summary>
    public const float UnityLevel = 1.0f;

    /// <summary>
    /// The level a folded channel carries until it is changed: 1/√2, the −3.01 dB a fold is balanced at, so that a
    /// signal reaching both sides of it keeps the level it had.
    /// </summary>
    public static readonly float DownmixLevel = MathF.Sqrt(0.5f);

    private const int MaximumTargetChannelCount = 2;

    private static readonly GroupSpec Mono = new("mono", "M", UnityLevel);
    private static readonly GroupSpec LeftRight = new("stereo", "L/R", UnityLevel);
    private static readonly GroupSpec Front = new("front", "FL/FR", UnityLevel);
    private static readonly GroupSpec Center = new("center", "FC", DownmixLevel);
    private static readonly GroupSpec LowFrequency = new("lfe", "LFE", 0.0f);
    private static readonly GroupSpec Side = new("side", "SL/SR", DownmixLevel);
    private static readonly GroupSpec Back = new("back", "BL/BR", DownmixLevel);
    private static readonly GroupSpec BackCenter = new("backCenter", "BC", DownmixLevel);
    private static readonly GroupSpec FrontOfCenter = new("frontOfCenter", "FLC/FRC", DownmixLevel);
    private static readonly GroupSpec TopFront = new("topFront", "TFL/TFR", DownmixLevel);

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
    internal static bool TryCreateRemix(ChannelLayout layout, int channelCount, IReadOnlyDictionary<string, float>? levels, out float[] gains)
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
            float level = LevelOf(route.Group, RemixLevelSet.Remix, levels);
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
    /// The coefficients that give a narrower stream the sides of a wider target: every channel of the target that sits on
    /// one side carries the source channel of that side, and the ones that belong to neither — a centre, a low frequency
    /// — carry nothing at all. A one-channel stream reaches both sides, which is what widening it means.
    /// </summary>
    internal static bool TryCreateUpmix(ChannelLayout layout, int channelCount, ChannelLayout targetLayout, int targetChannelCount, IReadOnlyDictionary<string, float>? levels, out FoldMatrix matrix)
    {
        matrix = default;

        bool mono = layout == ChannelLayout.Mono && channelCount == 1;
        bool stereo = layout == ChannelLayout.Stereo && channelCount == 2;
        if ((!mono && !stereo) || targetChannelCount <= channelCount || targetChannelCount < 2)
        {
            return false;
        }

        LayoutSpec? target = SpecOf(targetLayout);
        if (target is null || target.ChannelCount != targetChannelCount)
        {
            return false;
        }

        float level = LevelOf(mono ? Mono : LeftRight, RemixLevelSet.Remix, levels);
        float[][] rows = new float[targetChannelCount][];
        for (int channel = 0; channel < targetChannelCount; channel++)
        {
            rows[channel] = mono ? [0.0f] : [0.0f, 0.0f];
        }

        foreach (GroupRoute route in target.Routes)
        {
            foreach (int channel in route.Left)
            {
                if (mono || !route.Right.Contains(channel))
                {
                    rows[channel][0] = level;
                }
            }

            foreach (int channel in route.Right)
            {
                if (mono || !route.Left.Contains(channel))
                {
                    rows[channel][mono ? 0 : 1] = level;
                }
            }
        }

        matrix = new FoldMatrix(rows, targetChannelCount);
        return true;
    }

    /// <summary>
    /// The coefficients that fold a layout down to one or two channels, or false when the layout is one this table has
    /// no groups for, when the channel count disagrees with it, or when it is not wider than the target.
    /// </summary>
    internal static bool TryCreateDownmix(ChannelLayout layout, int channelCount, int targetChannelCount, IReadOnlyDictionary<string, float>? levels, out FoldMatrix matrix)
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
            float level = LevelOf(route.Group, RemixLevelSet.Downmix, levels);
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

            matrix = new FoldMatrix([left], 1);
            return true;
        }

        matrix = new FoldMatrix([left, right], targetChannelCount);
        return true;
    }

    /// <summary>
    /// The gain each of the two sides carries for a balance: the side the sound is moved away from is quietened, the
    /// other is left whole. A negative value moves the sound left, so it is the right side that gives way.
    /// </summary>
    internal static void PanGains(float pan, out float left, out float right)
    {
        float offset = Math.Clamp(pan, -1.0f, 1.0f);
        float amount = Math.Abs(offset);
        left = offset > 0 ? 1.0f - amount : 1.0f;
        right = offset < 0 ? 1.0f - amount : 1.0f;
    }

    /// <summary>
    /// The gain each of a layout's channels carries for a balance: the channels that sit on the side the sound moves
    /// away from are quietened, the ones on the other side and the ones that belong to neither — a centre, a low
    /// frequency — are left alone. Null when there is nothing to balance, which is one channel or a value of none.
    /// </summary>
    internal static float[]? TryCreatePanSides(ChannelLayout layout, int channelCount, float pan)
    {
        float offset = Math.Clamp(pan, -1.0f, 1.0f);
        if (offset == 0.0f || channelCount < 2)
        {
            return null;
        }

        PanGains(offset, out float left, out float right);

        float[] sides = new float[channelCount];
        Array.Fill(sides, 1.0f);

        LayoutSpec? spec = SpecOf(layout);
        if (spec is null || spec.ChannelCount != channelCount)
        {
            // Nothing says which channel is which, so the pair every arrangement starts with is the only guess to make.
            sides[0] = left;
            sides[1] = right;
            return sides;
        }

        foreach (GroupRoute route in spec.Routes)
        {
            foreach (int channel in route.Left)
            {
                if (!route.Right.Contains(channel))
                {
                    sides[channel] = left;
                }
            }

            foreach (int channel in route.Right)
            {
                if (!route.Left.Contains(channel))
                {
                    sides[channel] = right;
                }
            }
        }

        return sides;
    }

    /// <summary>
    /// The channel each one is exchanged with when the sides are swapped: every left channel with the right one of its
    /// pair. A layout the table does not describe swaps the pair every arrangement starts with, and one channel has
    /// nothing to swap.
    /// </summary>
    internal static int[] CreateInversion(ChannelLayout layout, int channelCount)
    {
        int[] exchange = new int[channelCount];
        for (int channel = 0; channel < channelCount; channel++)
        {
            exchange[channel] = channel;
        }

        if (channelCount < 2)
        {
            return exchange;
        }

        LayoutSpec? spec = SpecOf(layout);
        if (spec is null || spec.ChannelCount != channelCount)
        {
            exchange[0] = 1;
            exchange[1] = 0;
            return exchange;
        }

        foreach (GroupRoute route in spec.Routes)
        {
            if (route.Left.Length != route.Right.Length)
            {
                continue;
            }

            for (int index = 0; index < route.Left.Length; index++)
            {
                int left = route.Left[index];
                int right = route.Right[index];
                if (left == right || left >= channelCount || right >= channelCount)
                {
                    continue;
                }

                exchange[left] = right;
                exchange[right] = left;
            }
        }

        return exchange;
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
    /// The level a group carries: the one the listener chose, clamped to the range a gain may take, or the default of
    /// the set in charge when nothing was chosen.
    /// </summary>
    private static float LevelOf(GroupSpec group, RemixLevelSet set, IReadOnlyDictionary<string, float>? levels)
    {
        if (levels is not null && levels.TryGetValue(group.Key, out float level))
        {
            return Math.Clamp(level, 0.0f, 1.0f);
        }

        return set == RemixLevelSet.Remix ? UnityLevel : group.DownmixDefaultLevel;
    }

    /// <summary>One group of an arrangement and the channels of it that reach each side of the fold.</summary>
    private readonly record struct GroupRoute(GroupSpec Group, int[] Left, int[] Right);

    /// <summary>The coefficients that take one block of a layout to the channels it is widened to or folded onto.</summary>
    internal readonly record struct FoldMatrix(float[][] Rows, int ChannelCount)
    {
        internal int InputChannelCount => Rows.Length == 0 ? 0 : Rows[0].Length;
    }

    /// <summary>A group of speakers one slider covers, with the level the downmix set gives it.</summary>
    private sealed record GroupSpec(string Key, string Label, float DownmixDefaultLevel);

    /// <summary>The groups of an arrangement, in the order a mixing UI lists them.</summary>
    private sealed record LayoutSpec(ChannelLayout Layout, int ChannelCount, GroupRoute[] Routes);
}