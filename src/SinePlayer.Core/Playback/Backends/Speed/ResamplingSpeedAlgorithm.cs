// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Backends.Speed;

/// <summary>
/// The algorithm that lets the pitch follow the rate, like a tape: every output frame is read from where the file would be at that
/// point, so the same shape arrives in less time and goes up with it. Exact and cheap - nothing is invented - and what it does to
/// a voice is the reason the other algorithm exists.
/// </summary>
internal sealed class ResamplingSpeedAlgorithm : ISpeedAlgorithm
{
    /// <summary>
    /// Where in the source the next block starts, as a fraction of a frame. A block rarely divides into a whole number of output
    /// frames, and what is left over is the next block's first sample rather than a rounding error that accumulates.
    /// </summary>
    private double _carry;

    public void Reset(int sampleRate)
    {
        _carry = 0;
    }

    public int Apply(double[][] planes, int valuesPerFrame, int sourceFrames, double speed, double[][] output, int capacity)
    {
        double from = _carry;
        for (int plane = 0; plane < planes.Length; plane++)
        {
            Resample(planes[plane], sourceFrames, output[plane], capacity, valuesPerFrame, speed, from);
        }

        _carry = Math.Clamp(from + (capacity * speed) - sourceFrames, 0, speed);
        return capacity;
    }

    /// <summary>
    /// Writes the target frames from the source, each frame being <paramref name="valuesPerFrame"/> contiguous values, taking
    /// <paramref name="speed"/> source frames per target frame from <paramref name="from"/>.
    /// </summary>
    private static void Resample(double[] source, int sourceFrames, double[] target, int targetFrames, int valuesPerFrame, double speed, double from)
    {
        for (int frame = 0; frame < targetFrames; frame++)
        {
            double at = from + (frame * speed);
            if (at < 0)
            {
                at = 0;
            }

            if (speed >= 1.0)
            {
                // Faster: what falls between two target frames is averaged in, because picking one of every eight samples at eight
                // times would alias.
                int first = (int)Math.Floor(at);
                int last = (int)Math.Ceiling(at + speed);
                if (first >= sourceFrames)
                {
                    first = sourceFrames - 1;
                }

                if (last > sourceFrames)
                {
                    last = sourceFrames;
                }

                if (last <= first)
                {
                    last = first + 1;
                }

                double divisor = last - first;
                for (int value = 0; value < valuesPerFrame; value++)
                {
                    double sum = 0;
                    for (int index = first; index < last; index++)
                    {
                        sum += source[(index * valuesPerFrame) + value];
                    }

                    target[(frame * valuesPerFrame) + value] = sum / divisor;
                }
            }
            else
            {
                // Slower: the position falls between two source frames, and the sound is what lies between them.
                int first = (int)Math.Floor(at);
                int second = first + 1;
                if (first >= sourceFrames)
                {
                    first = sourceFrames - 1;
                }

                if (second >= sourceFrames)
                {
                    second = sourceFrames - 1;
                }

                if (first < 0)
                {
                    first = 0;
                }

                if (second < first)
                {
                    second = first;
                }

                double blend = at - Math.Floor(at);
                for (int value = 0; value < valuesPerFrame; value++)
                {
                    double low = source[(first * valuesPerFrame) + value];
                    double high = source[(second * valuesPerFrame) + value];
                    target[(frame * valuesPerFrame) + value] = low + ((high - low) * blend);
                }
            }
        }
    }
}
