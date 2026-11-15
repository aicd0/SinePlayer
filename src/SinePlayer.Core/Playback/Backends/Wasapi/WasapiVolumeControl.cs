// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;

using Windows.Win32.Media.Audio;

namespace SinePlayer.Core.Playback.Backends.Wasapi;

internal sealed class WasapiVolumeControl
{
    private long _volumeBits = BitConverter.DoubleToInt64Bits(1.0);
    private volatile bool _muted;
    private IAudioStreamVolume? _volume;
    private float[]? _levels;
    private double _applied = double.NaN;

    /// <summary>Whether there is a stream to give the level to, or whether it has to be applied to the audio instead.</summary>
    internal bool HasDevice => _volume is not null;

    internal double Volume
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _volumeBits));
        set
        {
            double level = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 1.0;
            Interlocked.Exchange(ref _volumeBits, BitConverter.DoubleToInt64Bits(level));
        }
    }

    internal bool IsMuted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>
    /// Points the level at the stream of the device that was just opened, or at nothing when that device has no stream
    /// volume. What the device was last told is forgotten, because a new stream starts at full scale.
    /// </summary>
    internal void Attach(IAudioStreamVolume? volume, int channelCount)
    {
        _levels = volume is null ? null : new float[Math.Max(1, channelCount)];
        _applied = double.NaN;
        _volume = volume;
    }

    /// <summary>
    /// Gives the device the level playback is set to, if it is not already the one it has. Called from the thread that
    /// owns the client, because the interface belongs to the apartment that opened it.
    /// </summary>
    internal OperationResult Apply()
    {
        if (_volume is not { } volume || _levels is not { } levels)
        {
            return OperationResult.Success();
        }

        // Muting is a level of zero, and the level the caller wanted stays here: unmuting needs nothing from the device.
        double level = IsMuted ? 0.0 : Volume;
        if (level == _applied)
        {
            return OperationResult.Success();
        }

        try
        {
            Array.Fill(levels, (float)level);
            volume.SetAllVolumes((uint)levels.Length, levels);
            _applied = level;
            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            return OperationResult.Failure($"Unable to set the level of the audio stream: {exception.Message}", exception);
        }
    }
}
