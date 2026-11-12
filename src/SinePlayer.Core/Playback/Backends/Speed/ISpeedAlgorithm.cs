// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Backends.Speed;

/// <summary>
/// One way of fitting a block of sound to the playback rate: it is handed the block's samples as numbers and answers with as many
/// as the media is worth at that rate. What the block is made of - which format, how many planes, whether the channels share one -
/// is the filter's business, not the algorithm's, so an algorithm only ever sees frames of values.
/// </summary>
internal interface ISpeedAlgorithm
{
    /// <summary>
    /// Takes the stream's sample rate, and lets go of whatever the last stream, rate or choice of algorithm left behind: an
    /// algorithm that works in windows sizes them from the rate, and one that carries a position starts it again from the first
    /// block it is handed.
    /// </summary>
    void Reset(int sampleRate);

    /// <summary>
    /// Fits the block to the rate. Every plane holds <paramref name="sourceFrames"/> frames of
    /// <paramref name="valuesPerFrame"/> values, laid out frame by frame, and the answer is written into
    /// <paramref name="output"/>, whose planes hold room for <paramref name="capacity"/> frames.
    /// </summary>
    /// <returns>How many frames were written, which is at most <paramref name="capacity"/>.</returns>
    int Apply(double[][] planes, int valuesPerFrame, int sourceFrames, double speed, double[][] output, int capacity);
}
