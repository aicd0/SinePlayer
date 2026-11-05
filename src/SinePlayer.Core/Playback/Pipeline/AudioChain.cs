// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class AudioChain(PipelineTimeline timeline, PipelineEvents events)
{
    public ComponentSlot<IAudioDecoder> Decoder { get; } = new();

    public ComponentSlot<IAudioRenderer> Renderer { get; } = new();

    public PacketQueue PacketQueue { get; } = new();

    public AudioQueue BufferQueue { get; } = new();

    public PipelineNodeAsync DecodeNode { get; } = new();

    public PipelineNode RenderNode { get; } = new();

    public AudioFilterChain Filters { get; } = new();

    public async Task Decode(Func<bool> stopRequested, Action wakeReader)
    {
        long workingEpoch = -1;

        while (true)
        {
            long stamp = DecodeNode.Stamp;
            if (stopRequested())
            {
                return;
            }

            if (Decoder.Update(out IAudioDecoder? oldAudioDecoder))
            {
                oldAudioDecoder?.Dispose();
            }

            long epoch = timeline.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    Decoder.CurrentComponent?.Flush();
                }
                catch (Exception ex)
                {
                    events.PostWarning($"The audio decoder could not be flushed: {ex.Message}");
                }

                continue;
            }

            if (!PacketQueue.TryPeek(out PacketEntry entry))
            {
                await DecodeNode.WaitAsync(stamp);
                continue;
            }

            PipelineVersion audioDecoderVersions = Decoder.CurrentVersions;
            VersionVerdict verdict = PipelineVersion.CompareAudioDecoder(entry.Versions, audioDecoderVersions);
            if (verdict == VersionVerdict.Block)
            {
                await DecodeNode.WaitAsync(stamp);
                continue;
            }

            if (!PacketQueue.TryDequeue(out _))
            {
                await DecodeNode.WaitAsync(stamp);
                continue;
            }

            wakeReader();

            MediaPacket? packet = entry.Packet;
            long packetEpoch = entry.Epoch;
            using (packet)
            {
                if (verdict == VersionVerdict.Drop || epoch != packetEpoch)
                {
                    continue;
                }

                var audioBufferVersions = PipelineVersion.Max(entry.Versions, audioDecoderVersions);
                IAudioDecoder? audioDecoder = Decoder.CurrentComponent;
                if (packet is null)
                {
                    OperationResult drained = DrainDecoder(audioDecoder, audioBufferVersions, packetEpoch);
                    if (!drained.IsSuccessful)
                    {
                        events.PostWarning(drained.Message);
                    }

                    BufferQueue.Enqueue(new AudioEntry(null, audioBufferVersions, epoch));
                    WakeRender();
                    continue;
                }

                if (audioDecoder is null)
                {
                    continue;
                }

                OperationResult routed = DecodePacket(audioDecoder, packet, audioBufferVersions, epoch);
                if (!routed.IsSuccessful)
                {
                    events.PostFailure(routed);
                    return;
                }
            }

            await Task.Yield();
        }
    }

    private OperationResult DecodePacket(IAudioDecoder audioDecoder, MediaPacket packet, PipelineVersion versions, long epoch)
    {
        OperationResult decoded;
        IReadOnlyList<RawAudioBuffer> buffers;
        try
        {
            decoded = audioDecoder.TryDecode(packet, out buffers);
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }

        if (!decoded.IsSuccessful)
        {
            return decoded;
        }

        if (buffers is null)
        {
            return OperationResult.Failure("The audio decoder returned no buffers.");
        }

        foreach (RawAudioBuffer buffer in buffers)
        {
            BufferQueue.Enqueue(new AudioEntry(buffer, versions, epoch));
        }

        if (buffers.Count > 0)
        {
            WakeRender();
        }

        return OperationResult.Success();
    }

    private OperationResult DrainDecoder(IAudioDecoder? decoder, PipelineVersion versions, long epoch)
    {
        if (decoder is null)
        {
            return OperationResult.Success();
        }

        OperationResult drained;
        IReadOnlyList<RawAudioBuffer> buffers;
        try
        {
            drained = decoder.Drain(out buffers);
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }

        if (!drained.IsSuccessful)
        {
            return drained;
        }

        if (buffers is null)
        {
            return OperationResult.Failure("The audio decoder returned no buffers.");
        }

        bool handedOver = false;
        foreach (RawAudioBuffer buffer in buffers)
        {
            BufferQueue.Enqueue(new AudioEntry(buffer, versions, epoch));
            handedOver = true;
        }

        if (handedOver)
        {
            WakeRender();
        }

        return OperationResult.Success();
    }

    private void WakeRender()
    {
        if (!BufferQueue.IsEmpty)
        {
            RenderNode.Wake();
        }
    }
}
