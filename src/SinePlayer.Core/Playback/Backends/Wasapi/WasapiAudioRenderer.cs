// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using System.Threading;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Timing;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace SinePlayer.Core.Playback.Backends.Wasapi;

/// <summary>
/// Plays audio on the default output device through WASAPI in shared mode.
/// <para>
/// The stream is opened with the decoder's own format and the audio engine is asked to convert, which
/// keeps sample rate conversion and speaker mixing out of the engine. The renderer's own work is
/// interleaving the decoder's planes and keeping the device buffer fed.
/// </para>
/// <para>
/// <see cref="Submit"/> hands over one block at a time and waits for the device when its buffer is
/// full, so it belongs on the thread the pipeline dedicates to audio rather than on a caller's thread.
/// </para>
/// </summary>
public sealed class WasapiAudioRenderer : IAudioRenderer
{
    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatIeeeFloat = 3;
    private const ushort WaveFormatExtensible = 0xFFFE;
    private const int ExtensibleExtraSize = 22;
    private const int MaxWaitMilliseconds = 2000;

    private const string TAG = nameof(WasapiAudioRenderer);

    /// <summary>The class id of the endpoint enumerator, which is not in the Win32 metadata.</summary>
    private static readonly Guid MMDeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    private IAudioClient? _client;
    private IAudioRenderClient? _renderClient;
    private ISimpleAudioVolume? _volume;
    private WasapiAudioClock? _clock;
    private WasapiAudioFormat.Descriptor _format;
    private byte[] _staging = [];
    private int _bufferFrameCount;
    private double _volumeLevel = 1.0;
    private bool _muted;
    private bool _disposed;
    private int _submittedBlocks;
    private long _underrunCount;

    /// <summary>
    /// How often the device ran dry between two submits. Every one of them is an audible gap, so this
    /// is the number that says whether the pipeline is keeping the device fed.
    /// </summary>
    public long UnderrunCount => Interlocked.Read(ref _underrunCount);

    /// <summary>
    /// How much audio the device can hold, in frames. This is the slack a late submit is absorbed by, so it
    /// is worth being able to see it rather than infer it from audible clicks.
    /// </summary>
    internal int BufferFrameCount => _bufferFrameCount;

    public string Name => "wasapi";

    public double Volume
    {
        get => _volumeLevel;
        set
        {
            _volumeLevel = Math.Clamp(value, 0.0, 1.0);
            try
            {
                _volume?.SetMasterVolume((float)_volumeLevel, Guid.Empty);
            }
            catch (Exception)
            {
                // A device that has gone away is reported by the next submit; volume is not worth
                // failing a caller over.
            }
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            try
            {
                _volume?.SetMute(value, Guid.Empty);
            }
            catch (Exception)
            {
                // As above: the failure surfaces where it matters, not here.
            }
        }
    }

    public IPlaybackClock Clock => _clock ?? throw new InvalidOperationException("The audio renderer has not been configured.");

    /// <summary>Frames the device holds that have not been played yet.</summary>
    public long QueuedSampleCount
    {
        get
        {
            IAudioClient? client = _client;
            if (client is null)
            {
                return 0;
            }

            try
            {
                client.GetCurrentPadding(out uint padding);
                return padding;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public PlaybackResult Configure(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (_disposed)
        {
            return PlaybackResult.Failure("The renderer has already been disposed.");
        }

        if (!WasapiAudioFormat.TryDescribe(stream, out WasapiAudioFormat.Descriptor format, out string descriptionError))
        {
            return PlaybackResult.Failure(descriptionError);
        }

        Release();

        try
        {
            // The engine creates its objects on threads of its own, so COM is initialised here rather
            // than assumed. An apartment the host already chose is left alone.
            unsafe
            {
                PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            }

            HRESULT created = PInvoke.CoCreateInstance(MMDeviceEnumeratorClassId, null, CLSCTX.CLSCTX_ALL, out IMMDeviceEnumerator enumerator);
            if (created.Value < 0)
            {
                return PlaybackResult.Failure($"Unable to reach the audio endpoint enumerator ({created.Value}).");
            }

            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out IMMDevice device);
            device.Activate(typeof(IAudioClient).GUID, CLSCTX.CLSCTX_ALL, null, out object activated);

            var client = (IAudioClient)activated;
            InitializeStream(client, format);
            client.GetBufferSize(out uint bufferFrameCount);
            client.GetService(typeof(IAudioRenderClient).GUID, out object renderService);
            client.GetService(typeof(IAudioClock).GUID, out object clockService);

            var audioClock = (IAudioClock)clockService;
            audioClock.GetFrequency(out ulong frequency);

            // Volume control is a convenience: a device that refuses the service still plays.
            try
            {
                client.GetService(typeof(ISimpleAudioVolume).GUID, out object volumeService);
                if (volumeService is ISimpleAudioVolume simpleVolume)
                {
                    _volume = simpleVolume;
                    _volume.SetMasterVolume((float)_volumeLevel, Guid.Empty);
                    _volume.SetMute(_muted, Guid.Empty);
                }
            }
            catch (Exception)
            {
                // Not every endpoint offers a session volume; playback does not depend on it.
            }

            _client = client;
            _renderClient = (IAudioRenderClient)renderService;
            _clock = new WasapiAudioClock(client, audioClock, frequency);
            _format = format;
            _bufferFrameCount = (int)bufferFrameCount;
            _staging = new byte[format.BlockAlign * 4096];

            // A new stream starts with an empty device and no history, so what the last one counted says
            // nothing about this one.
            _submittedBlocks = 0;
            Interlocked.Exchange(ref _underrunCount, 0);

            // The endpoint and the enumerator hold no state we need: the client keeps its own reference.
            return PlaybackResult.Success;
        }
        catch (Exception exception)
        {
            Release();
            return PlaybackResult.Failure($"Unable to open the audio device: {exception.Message}");
        }
    }

    public PlaybackResult Submit(RawAudioBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (_disposed)
        {
            return PlaybackResult.Failure("The renderer has already been disposed.");
        }

        IAudioRenderClient? renderClient = _renderClient;
        WasapiAudioClock? clock = _clock;
        if (renderClient is null || clock is null)
        {
            return PlaybackResult.Failure("The audio renderer has not been configured.");
        }

        int frames = buffer.SampleCount;
        if (frames <= 0)
        {
            return PlaybackResult.Success;
        }

        int required = _format.BlockAlign * frames;

        // An empty device buffer once playback has started means the engine left the device unfed since
        // the last submit, which is exactly what a listener hears as a glitch.
        if (_submittedBlocks > 0 && GetFreeFrameCount() >= (uint)_bufferFrameCount)
        {
            Interlocked.Increment(ref _underrunCount);
        }
        if (_staging.Length < required)
        {
            _staging = new byte[required];
        }

        if (!AudioInterleaver.TryInterleave(buffer, _staging, out int byteCount, out string error))
        {
            return PlaybackResult.Failure(error);
        }

        if (byteCount != required)
        {
            return PlaybackResult.Failure($"The audio block produced {byteCount} byte(s) where {required} were expected.");
        }

        // The stream has to be running before anything is written: a stopped WASAPI stream does not
        // consume its buffer, so the first block would fill it and the rest would wait forever.
        if (!clock.IsRunning)
        {
            clock.Start();
        }

        // The whole block is turned into device bytes once, then handed over in whatever chunks the
        // device has room for: a decoded block is normally larger than the device buffer, so insisting
        // on room for all of it would never succeed.
        int offset = 0;
        int waited = 0;
        while (offset < byteCount)
        {
            uint free = GetFreeFrameCount();
            if (free == 0)
            {
                // A stopped stream means the caller paused: the device keeps what it already holds and
                // resumes from there, so waiting is right and giving up would only lose audio.
                bool stopped = _clock is null || !_clock.IsRunning;
                if (!stopped && waited >= MaxWaitMilliseconds)
                {
                    return PlaybackResult.Failure("The audio device did not free its buffer in time.");
                }

                Thread.Sleep(1);
                waited = stopped ? 0 : waited + 1;
                continue;
            }

            int chunkFrames = Math.Min((int)free, (byteCount - offset) / _format.BlockAlign);
            int bytes = chunkFrames * _format.BlockAlign;
            try
            {
                unsafe
                {
                    renderClient.GetBuffer((uint)chunkFrames, out byte* destination);
                    Marshal.Copy(_staging, offset, (nint)destination, bytes);
                    renderClient.ReleaseBuffer((uint)chunkFrames, 0);
                }
            }
            catch (Exception exception)
            {
                return PlaybackResult.Failure($"Unable to queue audio: {exception.Message}");
            }

            offset += bytes;

            // The device accepted audio, so the next wait is a fresh one.
            waited = 0;
            _submittedBlocks++;
        }

        return PlaybackResult.Success;
    }

    public PlaybackResult Flush()
    {
        if (_disposed)
        {
            return PlaybackResult.Failure("The renderer has already been disposed.");
        }

        IAudioClient? client = _client;
        WasapiAudioClock? clock = _clock;
        if (client is null || clock is null)
        {
            return PlaybackResult.Failure("The audio renderer has not been configured.");
        }

        try
        {
            bool wasRunning = clock.IsRunning;
            TimeSpan position = clock.Position;
            if (wasRunning)
            {
                clock.Pause();
            }

            // Discards what the device still holds. The stream's position restarts, so the clock's base
            // is re-anchored at the position it was showing: flushing does not move playback.
            client.Reset();
            clock.SetPosition(position);

            // The device is empty by design now, so the next submit finding no audio in it is not a gap.
            _submittedBlocks = 0;

            if (wasRunning)
            {
                clock.Start();
            }

            return PlaybackResult.Success;
        }
        catch (Exception exception)
        {
            return PlaybackResult.Failure($"Unable to flush audio: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _client?.Stop();
        }
        catch (Exception)
        {
            // The device may already be gone; there is nothing left to stop in that case.
        }

        Release();
    }

    private static unsafe void InitializeStream(IAudioClient client, WasapiAudioFormat.Descriptor format)
    {
        // The engine converts whatever it is handed, so the renderer never resamples.
        uint flags = PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

        // How much audio the device can hold before it has nothing left to play. Left to the engine it is
        // one period — about ten milliseconds on a typical endpoint — and that is all the slack there is:
        // any longer delay in getting the next block there is silence the listener hears as a click. The
        // pipeline holds two seconds decoded, so the only way it can run dry is the thread handing blocks
        // over being kept off the processor, which nothing can promise will not happen. A fifth of a
        // second costs nothing here: the clock counts what has been played, so the picture stays in step
        // whatever the buffer holds, and a pause or a seek discards it outright.
        const long deviceBufferDuration = 2_000_000;

        if (TryInitializeWithDuration(client, format, flags, deviceBufferDuration))
        {
            return;
        }

        // An endpoint that will not take the longer buffer still has to play.
        TryInitializeWithDuration(client, format, flags, 0);
    }

    private static unsafe bool TryInitializeWithDuration(IAudioClient client, WasapiAudioFormat.Descriptor format, uint flags, long bufferDuration)
    {
        try
        {
            if (format.IsExtensible)
            {
                WAVEFORMATEXTENSIBLE extensible = default;
                extensible.Format.wFormatTag = WaveFormatExtensible;
                extensible.Format.nChannels = (ushort)format.ChannelCount;
                extensible.Format.nSamplesPerSec = (uint)format.SampleRate;
                extensible.Format.nAvgBytesPerSec = (uint)format.AverageBytesPerSecond;
                extensible.Format.nBlockAlign = (ushort)format.BlockAlign;
                extensible.Format.wBitsPerSample = (ushort)format.BitsPerSample;
                extensible.Format.cbSize = ExtensibleExtraSize;
                extensible.Samples.wValidBitsPerSample = (ushort)format.BitsPerSample;
                extensible.dwChannelMask = format.ChannelMask;
                extensible.SubFormat = format.IsFloat ? SubTypeIeeeFloat : SubTypePcm;

                // An extensible descriptor opens with a WAVEFORMATEX, so one pointer serves for both; that
                // is the layout the platform expects to be handed.
                client.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, bufferDuration, 0, (WAVEFORMATEX*)&extensible);
                return true;
            }

            WAVEFORMATEX simple = default;
            simple.wFormatTag = format.IsFloat ? WaveFormatIeeeFloat : WaveFormatPcm;
            simple.nChannels = (ushort)format.ChannelCount;
            simple.nSamplesPerSec = (uint)format.SampleRate;
            simple.nAvgBytesPerSec = (uint)format.AverageBytesPerSecond;
            simple.nBlockAlign = (ushort)format.BlockAlign;
            simple.wBitsPerSample = (ushort)format.BitsPerSample;
            simple.cbSize = 0;
            client.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, bufferDuration, 0, &simple);
            return true;
        }
        catch (Exception exception)
        {
            Logger.W(TAG, $"Unable to open the audio device with a buffer of {bufferDuration / 10_000} ms: {exception.Message}");
            return false;
        }
    }

    private uint GetFreeFrameCount()
    {
        IAudioClient? client = _client;
        if (client is null)
        {
            return 0;
        }

        client.GetCurrentPadding(out uint padding);
        return padding >= (uint)_bufferFrameCount ? 0 : (uint)_bufferFrameCount - padding;
    }

    private void Release()
    {
        _clock = null;
        _renderClient = null;
        _volume = null;
        _client = null;
        _bufferFrameCount = 0;
    }
}

