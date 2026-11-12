// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Backends.Speed;

/// <summary>
/// The algorithm that keeps the pitch: nothing is resampled, the sound is re-cut, windows of it laid over one another so that the
/// same shapes arrive sooner or later. Every window is placed where it best continues the one before it - two windows of a tone
/// that disagree about its phase cancel each other where they overlap, which is heard as hollowness and warble, and choosing the
/// placement by correlation is what avoids it.
/// <para>
/// A window has to be filled before it can be laid down, so this costs about a window of latency: the sound lags the file by that
/// much, once, and not by more. What a block carries is still its media over the rate, because the frames come out of a queue that
/// the windows fill and that the blocks drain in the same proportion.
/// </para>
/// </summary>
internal sealed class WsolaSpeedAlgorithm : ISpeedAlgorithm
{
    private const int WindowMilliseconds = 20;
    private const int SearchMilliseconds = 5;
    private const int SearchStep = 4;

    private double[][] _history = [];
    private double[][] _overlap = [];
    private double[][] _queue = [];
    private double[] _window = [];

    private int _planes;
    private int _valuesPerFrame;
    private int _sampleRate;
    private int _windowFrames;
    private int _hopFrames;
    private int _searchFrames;
    private int _frames;
    private int _queuedFrames;
    private long _origin;
    private double _position;
    private long _natural;
    private bool _started;

    /// <summary>
    /// Sizes the window to the stream. A shorter window keeps transients together and costs less; a longer one hides more of the
    /// seams at the price of smearing them. Twenty milliseconds is the usual compromise for speech.
    /// </summary>
    public void Reset(int sampleRate)
    {
        int window = sampleRate > 0 ? (sampleRate * WindowMilliseconds) / 1000 : 0;
        if (window != _windowFrames || sampleRate != _sampleRate)
        {
            _windowFrames = window;
            _hopFrames = window / 2;
            _searchFrames = Math.Max(1, (sampleRate * SearchMilliseconds) / 1000);
            _window = new double[window];
            for (int frame = 0; frame < window; frame++)
            {
                // A Hann window: at half a window of hop these add up to one, so a level that goes in comes out the same.
                _window[frame] = 0.5 - (0.5 * Math.Cos((2 * Math.PI * frame) / window));
            }
        }

        _sampleRate = sampleRate;
        _history = [];
        _overlap = [];
        _queue = [];
        _planes = 0;
        _valuesPerFrame = 0;
        _frames = 0;
        _queuedFrames = 0;
        _origin = 0;
        _position = 0;
        _natural = 0;
        _started = false;
    }

    /// <summary>
    /// Fits the block to the rate by laying windows of it over one another. The block carries what its media comes to over the
    /// rate, taken from the queue those windows fill; a window is only laid down once there is enough sound to fill it, so the
    /// queue starts a window behind and stays that much behind.
    /// </summary>
    public int Apply(double[][] planes, int valuesPerFrame, int sourceFrames, double speed, double[][] output, int capacity)
    {
        if (_windowFrames <= 0)
        {
            // A rate too low to hold a window: there is nothing to lay over anything.
            return 0;
        }

        if (_planes != planes.Length || _valuesPerFrame != valuesPerFrame)
        {
            Prepare(planes.Length, valuesPerFrame);
        }

        int at = _frames * valuesPerFrame;
        for (int plane = 0; plane < planes.Length; plane++)
        {
            HoldFrame(plane, planes[plane], at, sourceFrames * valuesPerFrame);
        }

        _frames += sourceFrames;

        long end = _origin + _frames;
        double advance = _hopFrames * speed;
        while (_queuedFrames < capacity)
        {
            long nominal = (long)Math.Round(_position);
            if (nominal + _searchFrames + _windowFrames > end)
            {
                break;
            }

            long start = _started ? ChooseStart(nominal) : nominal;
            Overlap(planes.Length, start);
            _natural = start + _hopFrames;
            _position += advance;
            _started = true;
        }

        int produced = Math.Min(capacity, _queuedFrames);
        int producedValues = produced * valuesPerFrame;
        for (int plane = 0; plane < planes.Length; plane++)
        {
            Array.Copy(_queue[plane], 0, output[plane], 0, producedValues);
        }

        DropQueue(produced);
        Trim();
        return produced;
    }

    /// <summary>
    /// Opens a stream on a window of silence in front of it. The first window has nothing before it to be faded in against, so the
    /// cross-fade would otherwise begin the sound with a ramp; against silence it begins with silence instead, which is what the
    /// latency costs and all it costs.
    /// </summary>
    private void Prepare(int planeCount, int valuesPerFrame)
    {
        _planes = planeCount;
        _valuesPerFrame = valuesPerFrame;
        _history = new double[planeCount][];
        _overlap = new double[planeCount][];
        _queue = new double[planeCount][];
        for (int plane = 0; plane < planeCount; plane++)
        {
            _history[plane] = new double[_windowFrames * valuesPerFrame];
            _overlap[plane] = new double[_hopFrames * valuesPerFrame];
            _queue[plane] = new double[_windowFrames * valuesPerFrame];
        }

        _frames = _windowFrames;
        _queuedFrames = 0;
        _origin = 0;
        _position = 0;
        _natural = 0;
        _started = false;
    }

    private void HoldFrame(int plane, double[] values, int at, int valueCount)
    {
        double[] history = _history[plane];
        int needed = at + valueCount;
        if (history.Length < needed)
        {
            Array.Resize(ref history, Math.Max(needed, history.Length * 2));
            _history[plane] = history;
        }

        Array.Copy(values, 0, history, at, valueCount);
    }

    /// <summary>
    /// Where to take the window that overlaps the last one. The nominal position is where it would go if the sound were simply
    /// being cut; the search looks either side of it for the place whose start best continues what the previous window ended
    /// with, which is the difference between a stretch that sounds like the same voices and one that sounds like a broken machine.
    /// </summary>
    private long ChooseStart(long nominal)
    {
        double[] history = _history[0];
        int values = _valuesPerFrame;
        int hop = _hopFrames;
        int wantedAt = (int)(_natural - _origin) * values;

        long best = nominal;
        double bestScore = double.NegativeInfinity;

        // Only a window there is sound for may be chosen. Below half speed the analysis advances by less than the search reaches
        // back, so the first windows of a stream would otherwise look for their predecessors before anything that is held.
        long lowest = Math.Max(nominal - _searchFrames, _origin);
        long highest = Math.Min(nominal + _searchFrames, _origin + _frames - _windowFrames);

        for (long offset = -_searchFrames; offset <= _searchFrames; offset += SearchStep)
        {
            long start = nominal + offset;
            if (start < lowest || start > highest)
            {
                continue;
            }

            int candidateAt = (int)(start - _origin) * values;
            double likeness = 0;
            double energy = 0;
            for (int frame = 0; frame < hop; frame++)
            {
                double candidate = history[candidateAt + (frame * values)];
                double wanted = history[wantedAt + (frame * values)];
                likeness += candidate * wanted;
                energy += candidate * candidate;
            }

            if (energy <= 0)
            {
                continue;
            }

            double score = likeness / Math.Sqrt(energy);
            if (score > bestScore)
            {
                bestScore = score;
                best = start;
            }
        }

        return best;
    }

    /// <summary>
    /// Lays one window over the last: the tail of that is added to the head of this, which is what makes one continuous sound out
    /// of windows taken from different places. The frames are left in the queue the block drains.
    /// </summary>
    private void Overlap(int planeCount, long start)
    {
        int values = _valuesPerFrame;
        int hop = _hopFrames;
        int at = _queuedFrames * values;

        for (int plane = 0; plane < planeCount; plane++)
        {
            double[] history = _history[plane];
            double[] overlap = _overlap[plane];
            double[] queue = _queue[plane];
            int needed = at + (hop * values);
            if (queue.Length < needed)
            {
                Array.Resize(ref queue, Math.Max(needed, queue.Length * 2));
                _queue[plane] = queue;
            }

            int windowAt = (int)(start - _origin) * values;
            for (int frame = 0; frame < hop; frame++)
            {
                int headAt = windowAt + (frame * values);
                int tailAt = headAt + (hop * values);
                for (int value = 0; value < values; value++)
                {
                    int into = at + (frame * values) + value;
                    queue[into] = overlap[(frame * values) + value] + (_window[frame] * history[headAt + value]);
                    overlap[(frame * values) + value] = _window[hop + frame] * history[tailAt + value];
                }
            }
        }

        _queuedFrames += hop;
    }

    private void DropQueue(int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        int values = _valuesPerFrame;
        int left = Math.Max(0, _queuedFrames - frames);
        if (left > 0)
        {
            for (int plane = 0; plane < _planes; plane++)
            {
                Array.Copy(_queue[plane], frames * values, _queue[plane], 0, left * values);
            }
        }

        _queuedFrames = left;
    }

    /// <summary>Lets go of the frames no window can reach any more, so the history is a window rather than the whole file.</summary>
    private void Trim()
    {
        long reachable = Math.Min((long)Math.Round(_position), _natural) - _searchFrames;
        int drop = (int)Math.Clamp(reachable - _origin, 0, _frames);
        if (drop <= 0)
        {
            return;
        }

        int values = _valuesPerFrame;
        for (int plane = 0; plane < _planes; plane++)
        {
            double[] history = _history[plane];
            Array.Copy(history, drop * values, history, 0, (_frames - drop) * values);
        }

        _origin += drop;
        _frames -= drop;
    }
}
