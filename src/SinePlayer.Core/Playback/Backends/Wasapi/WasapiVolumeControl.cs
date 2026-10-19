// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Windows.Win32.Media.Audio;

namespace SinePlayer.Core.Playback.Backends.Wasapi;

/// <summary>
/// The volume of one stream, behind one seam: what a test can replace to check the arithmetic without a device.
/// </summary>
internal interface IWasapiStreamVolume
{
    /// <summary>Applies one level, 0 to 1, to every channel of the stream.</summary>
    void SetAllChannels(float level);
}

/// <summary>
/// The sound of one renderer, at its own level.
/// <para>
/// This is the <em>stream's</em> volume and not the session's: a session belongs to the process, so two players
/// sharing one session would share one volume slider and one mute button — turning one down would turn the other
/// down with it. WASAPI offers no per-stream mute, so mute is applied as a level of zero and the level the user
/// set is left untouched, which is what makes unmuting restore it.
/// </para>
/// </summary>
internal sealed class WasapiVolumeControl
{
    private readonly IWasapiStreamVolume _stream;

    internal WasapiVolumeControl(IWasapiStreamVolume stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Hands the stream what it should play at: the level the user set, or zero while muted. Out-of-range levels
    /// are clamped rather than refused, because a volume is a request and not a command that can fail.
    /// </summary>
    internal void Apply(float level, bool muted)
    {
        _stream.SetAllChannels(muted ? 0f : Math.Clamp(level, 0f, 1f));
    }

    /// <summary>Wraps the device's own per-stream volume service, which is what makes two players independent.</summary>
    internal static WasapiVolumeControl Create(IAudioStreamVolume volume)
    {
        volume.GetChannelCount(out uint channelCount);
        return new WasapiVolumeControl(new StreamVolume(volume, channelCount));
    }

    private sealed class StreamVolume : IWasapiStreamVolume
    {
        private readonly IAudioStreamVolume _volume;
        private readonly uint _channelCount;

        internal StreamVolume(IAudioStreamVolume volume, uint channelCount)
        {
            _volume = volume;
            _channelCount = channelCount > 0 ? channelCount : 1;
        }

        public void SetAllChannels(float level)
        {
            // Per channel rather than the array call: the same effect, and it does not depend on how the
            // interop generator chose to express a pointer-and-count pair.
            for (uint channel = 0; channel < _channelCount; channel++)
            {
                _volume.SetChannelVolume(channel, level);
            }
        }
    }
}
