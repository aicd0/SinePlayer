// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Buffers;
using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Utils;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace SinePlayer.Core.Playback.Backends.Wasapi;

public sealed partial class WasapiAudioRenderer : IAudioRenderer
{
    private const string TAG = nameof(WasapiAudioRenderer);

    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatIeeeFloat = 3;
    private const ushort WaveFormatExtensible = 0xFFFE;
    private const int ExtensibleExtraSize = 22;

    private static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly string? _endpointId;
    private readonly string? _endpointName;
    private readonly bool _endpointIsDefault;

    private IAudioClient? _client;
    private IAudioRenderClient? _renderClient;
    private WasapiAudioClock? _clock;
    private WasapiAudioFormat.Descriptor _format;
    private int _bufferFrameCount;
    private int? _deviceChannelCount;
    private bool _disposed;
    private int _submittedBlocks;
    private long _underrunCount;

    public WasapiAudioRenderer(string? endpointId = null, string? endpointName = null, bool endpointIsDefault = false)
    {
        _endpointId = endpointId;
        _endpointName = endpointName;
        _endpointIsDefault = endpointIsDefault;
    }

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

    public string Name => _endpointName is null
        ? "WASAPI"
        : _endpointIsDefault ? $"WASAPI [{_endpointName}] (default)" : $"WASAPI [{_endpointName}]";

    public IPlaybackClock Clock => _clock ?? throw new InvalidOperationException("The audio renderer has not been configured.");

    /// <summary>
    /// The format this endpoint plays without the system converting it: the stream itself when the device has the
    /// speakers for it, or the same stream narrowed to what it has. The engine's own conversion is not the answer
    /// here — it divides the level down until no arrangement of the source could clip, which is about ten decibels
    /// quieter than the file — so the pipeline folds the mix to this format and the device is opened for the result.
    /// A stream that cannot be folded is still opened as it is, which is the behaviour this method exists to avoid
    /// wherever it can.
    /// </summary>
    public OperationResult<AudioStreamInfo> Negotiate(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        OperationResultCreator<AudioStreamInfo> result = new();
        if (!TryGetEndpointChannelCount(out int channels, out string error))
        {
            return result.Failure(error);
        }

        if (channels <= 0 || channels >= stream.ChannelCount)
        {
            return result.Success(stream);
        }

        ChannelLayout layout = channels switch
        {
            1 => ChannelLayout.Mono,
            2 => ChannelLayout.Stereo,
            _ => stream.ChannelLayout,
        };

        return result.Success(stream.WithLayout(channels, layout));
    }

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
            catch (Exception exception)
            {
                // The failure is preserved, not described: what the padding read threw is the diagnosis, and the
                // next hand-off or flush reports it as a pipeline error rather than a crash.
                _clock?.ReportDeviceLost(exception);
                return 0;
            }
        }
    }

    public OperationResult Configure(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (_disposed)
        {
            return OperationResult.Failure("The renderer has already been disposed.");
        }

        if (!WasapiAudioFormat.TryDescribe(stream, out WasapiAudioFormat.Descriptor format, out string descriptionError))
        {
            return OperationResult.Failure(descriptionError);
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

            HRESULT created = PInvoke.CoCreateInstance(WasapiAudioEndpoints.MMDeviceEnumeratorClassId, null, CLSCTX.CLSCTX_ALL, out IMMDeviceEnumerator enumerator);
            if (created.Value < 0)
            {
                return OperationResult.Failure($"Unable to reach the audio endpoint enumerator ({created.Value}).");
            }

            // The device the caller chose, or the one Windows would choose: the only difference between the two
            // is which lookup is asked, so a named endpoint behaves exactly like the default one from here on.
            IMMDevice device;
            try
            {
                if (_endpointId is null)
                {
                    enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device);
                }
                else
                {
                    enumerator.GetDevice(_endpointId, out device);
                }
            }
            catch (Exception ex)
            {
                return OperationResult.Failure(ex);
            }

            device.Activate(typeof(IAudioClient).GUID, CLSCTX.CLSCTX_ALL, null, out object activated);

            var client = (IAudioClient)activated;
            InitializeStream(client, format);
            client.GetBufferSize(out uint bufferFrameCount);
            client.GetService(typeof(IAudioRenderClient).GUID, out object renderService);
            client.GetService(typeof(IAudioClock).GUID, out object clockService);

            var audioClock = (IAudioClock)clockService;
            audioClock.GetFrequency(out ulong frequency);

            _client = client;
            _renderClient = (IAudioRenderClient)renderService;
            _clock = new WasapiAudioClock(client, audioClock, frequency);
            _format = format;
            _bufferFrameCount = (int)bufferFrameCount;

            // A new stream starts with an empty device and no history, so what the last one counted says
            // nothing about this one.
            _submittedBlocks = 0;
            Interlocked.Exchange(ref _underrunCount, 0);

            // The endpoint and the enumerator hold no state we need: the client keeps its own reference.
            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            Release();
            return OperationResult.Failure($"Unable to open the audio device: {exception.Message}");
        }
    }

    /// <inheritdoc/>
    public IAudioSubmit Submit(RawAudioBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (_disposed)
        {
            return new FinishedAudioSubmit(OperationResult.Failure("The renderer has already been disposed."));
        }

        IAudioRenderClient? renderClient = _renderClient;
        if (renderClient is null)
        {
            return new FinishedAudioSubmit(OperationResult.Failure("The audio renderer has not been configured."));
        }

        if (_clock is WasapiAudioClock lost && lost.DeviceLost)
        {
            // The device cannot be used, so the block cannot be handed over and no amount of waiting will
            // change that. The original failure is carried rather than described, and it is what turns this
            // into the pipeline's error instead of a thread waiting for a device that is never coming back.
            return new FinishedAudioSubmit(DeviceFailure(lost, "Unable to hand audio to the device"));
        }

        int frames = buffer.SampleCount;
        if (frames <= 0)
        {
            return new FinishedAudioSubmit(OperationResult.Success());
        }

        int required = _format.BlockAlign * frames;

        // An empty device buffer once playback has started means the engine left the device unfed since
        // the last submit, which is exactly what a listener hears as a glitch.
        if (_submittedBlocks > 0 && GetFreeFrameCount() >= (uint)_bufferFrameCount)
        {
            Interlocked.Increment(ref _underrunCount);
        }

        // The block is turned into device bytes once, here, for two reasons: the hand-off then holds nothing
        // but bytes, so the caller's buffer is free the moment this returns; and the conversion cannot fail
        // halfway through a device write, where the failure would be indistinguishable from a short one.
        byte[] staging = ArrayPool<byte>.Shared.Rent(required);
        if (!AudioInterleaver.TryInterleave(buffer, staging, out int byteCount, out string error))
        {
            ArrayPool<byte>.Shared.Return(staging);
            return new FinishedAudioSubmit(OperationResult.Failure(error));
        }

        if (byteCount != required)
        {
            ArrayPool<byte>.Shared.Return(staging);
            return new FinishedAudioSubmit(OperationResult.Failure($"The audio block produced {byteCount} byte(s) where {required} were expected."));
        }

        // A stopped stream is written into rather than started: it keeps what it is given, plays it from the
        // first frame when it is started, and its buffer filling up is what makes the hand-off yield. Whether
        // the stream runs is the pipeline's decision and nothing here may make it: a hand-off that started
        // the stream would release a hold on the clock that the picture depends on.
        return new WasapiAudioSubmit(this, renderClient, staging, byteCount);
    }

    public OperationResult Flush()
    {
        if (_disposed)
        {
            return OperationResult.Failure("The renderer has already been disposed.");
        }

        IAudioClient? client = _client;
        WasapiAudioClock? clock = _clock;
        if (client is null || clock is null)
        {
            return OperationResult.Failure("The audio renderer has not been configured.");
        }

        if (clock.DeviceLost)
        {
            return DeviceFailure(clock, "Unable to flush audio");
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

            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            // A reset that fails is recorded as well as reported, so the next hand-off refuses rather than
            // trying again — and the exception itself is what is reported.
            clock.ReportDeviceLost(exception);
            return OperationResult.Failure($"Unable to flush audio: {exception.Message}", exception);
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
        catch (Exception exception)
        {
            // The device may already be gone; there is nothing left to stop in that case, and a dispose that
            // threw would fault the teardown. The exception is written out whole all the same.
            PlaybackLogger.W(TAG, $"Unable to stop the audio device: {exception}");
        }

        Release();
    }

    /// <summary>
    /// How many channels the endpoint mixes in, or 0 when it cannot be read: a device whose format is a mystery is
    /// one to leave the conversion to, rather than one to guess a layout for. The answer is kept because it is a
    /// property of the device rather than of a stream, and asking opens a client of its own.
    /// </summary>
    private unsafe bool TryGetEndpointChannelCount(out int channelCount, out string error)
    {
        channelCount = 0;
        error = string.Empty;
        if (_deviceChannelCount is int known)
        {
            channelCount = known;
            return true;
        }

        int channels;
        try
        {
            PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            PInvoke.CoCreateInstance(WasapiAudioEndpoints.MMDeviceEnumeratorClassId, null, CLSCTX.CLSCTX_ALL, out IMMDeviceEnumerator enumerator);
            IMMDevice device;
            if (_endpointId is null)
            {
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device);
            }
            else
            {
                enumerator.GetDevice(_endpointId, out device);
            }

            device.Activate(typeof(IAudioClient).GUID, CLSCTX.CLSCTX_ALL, null, out object activated);
            var client = (IAudioClient)activated;
            client.GetMixFormat(out WAVEFORMATEX* mix);
            try
            {
                channels = mix->nChannels;
            }
            finally
            {
                Marshal.FreeCoTaskMem((nint)mix);
            }
        }
        catch (Exception exception)
        {
            // Not remembered: a device that has gone away can come back, and a cached "no" would outlive it.
            PlaybackLogger.W(TAG, $"Unable to read the format the audio device mixes in: {exception.Message}");
            error = $"Unable to reach the audio output device: {exception.Message}";
            return false;
        }

        _deviceChannelCount = channels;
        channelCount = channels;
        return true;
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
            PlaybackLogger.W(TAG, $"Unable to open the audio device with a buffer of {bufferDuration / 10_000} ms: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// A failure to use a device that has already refused once. The context says what was being attempted and
    /// the exception is carried whole, because only its own message and stack can say what actually happened.
    /// </summary>
    private static OperationResult DeviceFailure(WasapiAudioClock clock, string context)
    {
        Exception? cause = clock.Cause;
        return cause is null
            ? OperationResult.Failure($"{context}: the audio device cannot be used.")
            : OperationResult.Failure($"{context}: {cause.Message}", cause);
    }

    private uint GetFreeFrameCount()
    {
        IAudioClient? client = _client;
        if (client is null)
        {
            return 0;
        }

        try
        {
            client.GetCurrentPadding(out uint padding);
            return padding >= (uint)_bufferFrameCount ? 0 : (uint)_bufferFrameCount - padding;
        }
        catch (Exception exception)
        {
            // No room is the conservative answer for a device that cannot say how full it is, and the failure
            // is preserved so the hand-off that asked fails on its next step instead of waiting for room for
            // ever.
            _clock?.ReportDeviceLost(exception);
            return 0;
        }
    }

    private void OnChunkWritten()
    {
        _submittedBlocks++;
    }

    private void Release()
    {
        // The clock is let go of before the device is: nothing outside the renderer reads it any more, and a reader
        // that still holds it must not reach into a client this method is about to drop.
        _clock?.OnDeviceReleased();
        _clock = null;
        _renderClient = null;
        _client = null;
        _bufferFrameCount = 0;
    }

    /// <summary>
    /// One block being handed to the device. It writes what the device has room for and returns, so the
    /// caller owns every wait (<see cref="IAudioSubmit"/>); nothing here sleeps, retries or gives up.
    /// Its state is the block's own — the bytes, how far through them it is — so two hand-offs cannot
    /// share a buffer and an abandoned one leaves nothing behind in the renderer.
    /// </summary>
    private sealed class WasapiAudioSubmit : IAudioSubmit
    {
        private readonly WasapiAudioRenderer _owner;
        private readonly IAudioRenderClient _renderClient;
        private readonly byte[] _staging;
        private readonly int _byteCount;
        private int _offset;
        private bool _finished;
        private OperationResult _result = OperationResult.Success();
        private AudioSubmitYield _current;

        internal WasapiAudioSubmit(WasapiAudioRenderer owner, IAudioRenderClient renderClient, byte[] staging, int byteCount)
        {
            _owner = owner;
            _renderClient = renderClient;
            _staging = staging;
            _byteCount = byteCount;
        }

        public AudioSubmitYield Current => _current;

        public OperationResult Result => _result;

        public bool MoveNext()
        {
            if (_finished)
            {
                return false;
            }

            if (_owner._clock is WasapiAudioClock lost && lost.DeviceLost)
            {
                Finish(DeviceFailure(lost, "Unable to hand audio to the device"));
                return false;
            }

            while (true)
            {
                if (_offset >= _byteCount)
                {
                    Finish(OperationResult.Success());
                    return false;
                }

                uint free = _owner.GetFreeFrameCount();
                if (free == 0)
                {
                    if (_owner._clock is WasapiAudioClock gone && gone.DeviceLost)
                    {
                        // No room because the padding read threw, not because the device is full: yielding
                        // here would wait for a device that is never coming back.
                        Finish(DeviceFailure(gone, "Unable to hand audio to the device"));
                        return false;
                    }

                    // The device is full, or stopped. Which of those it is cannot be told from here and does
                    // not need to be: the caller can see the state and the position, and its wait is the one
                    // a pause, a seek and a stop can end.
                    _current = AudioSubmitYield.NoRoom;
                    return true;
                }

                int chunkFrames = Math.Min((int)free, (_byteCount - _offset) / _owner._format.BlockAlign);
                if (chunkFrames <= 0)
                {
                    Finish(OperationResult.Failure($"The audio block left {_byteCount - _offset} byte(s) that no whole frame can be made of."));
                    return false;
                }

                int bytes = chunkFrames * _owner._format.BlockAlign;
                try
                {
                    unsafe
                    {
                        _renderClient.GetBuffer((uint)chunkFrames, out byte* destination);
                        Marshal.Copy(_staging, _offset, (nint)destination, bytes);
                        _renderClient.ReleaseBuffer((uint)chunkFrames, 0);
                    }
                }
                catch (Exception exception)
                {
                    _owner._clock?.ReportDeviceLost(exception);
                    Finish(OperationResult.Failure($"Unable to queue audio: {exception.Message}", exception));
                    return false;
                }

                _offset += bytes;
                _owner.OnChunkWritten();
            }
        }

        /// <summary>
        /// Abandons a hand-off that is still suspended. What is already in the device stays there — a
        /// renderer cannot take it back — which is why the caller flushes before it feeds another position.
        /// </summary>
        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_staging);
            _finished = true;
        }

        private void Finish(OperationResult result)
        {
            _result = result;
            _finished = true;
        }
    }

    /// <summary>A hand-off that was over before it began: nothing to write, or something that went wrong.</summary>
    private sealed class FinishedAudioSubmit : IAudioSubmit
    {
        private readonly OperationResult _result;

        internal FinishedAudioSubmit(OperationResult result)
        {
            _result = result;
        }

        public AudioSubmitYield Current => AudioSubmitYield.NoRoom;

        public OperationResult Result => _result;

        public bool MoveNext()
        {
            return false;
        }

        public void Dispose()
        {
        }
    }
}

