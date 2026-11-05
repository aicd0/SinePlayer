// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class SubtitleChain(PipelineTimeline timeline, PipelineEvents events)
{
    private const int SubtitleChangeIntervalMilliseconds = 16;

    public ComponentSlot<ISubtitleDecoder> Decoder { get; } = new();

    public ComponentSlot<ISubtitleRenderer> Renderer { get; } = new();

    public PacketQueue PacketQueue { get; } = new();

    public SubtitleCueWindow CueWindow { get; } = new();

    public PipelineNodeAsync DecodeNode { get; } = new();

    public PipelineNode RenderNode { get; } = new();

    public SeqLock<SubtitlePresentation> Presentation { get; } = new(SubtitlePresentation.Identity);

    public IVideoRenderTarget? Target { get; set; }

    public IPictureFrameSource? LayoutSource { get; set; }

    public long SurfaceVersion;

    public void WakeDecode()
    {
        if (!PacketQueue.IsEmpty)
        {
            DecodeNode.Wake();
        }
    }

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

            if (Decoder.Update(out ISubtitleDecoder? oldSubtitleDecoder))
            {
                oldSubtitleDecoder?.Dispose();
                CueWindow.Reset();
            }

            EpochGate epochGate = timeline.Gate;
            long epoch = epochGate.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    Decoder.CurrentComponent?.Flush();
                }
                catch (Exception ex)
                {
                    events.PostWarning($"The subtitle decoder could not be flushed: {ex.Message}");
                }

                CueWindow.SeekTo(epoch, epochGate.SeekPosition);
                continue;
            }

            if (!PacketQueue.TryPeek(out PacketEntry entry))
            {
                await DecodeNode.WaitAsync(stamp);
                continue;
            }

            PipelineVersion subtitleDecoderVersions = Decoder.CurrentVersions;
            VersionVerdict verdict = PipelineVersion.CompareSubtitleDecoder(entry.Versions, subtitleDecoderVersions);
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

                ISubtitleDecoder? subtitleDecoder = Decoder.CurrentComponent;
                if (packet is null || subtitleDecoder is null)
                {
                    continue;
                }

                OperationResult routed = DecodeCues(epoch, subtitleDecoder, packet);
                if (!routed.IsSuccessful)
                {
                    events.PostFailure(routed);
                    return;
                }
            }

            await Task.Yield();
        }
    }

    private OperationResult DecodeCues(long epoch, ISubtitleDecoder subtitleDecoder, MediaPacket packet)
    {
        OperationResult decoded;
        IReadOnlyList<SubtitleCue> cues;
        try
        {
            decoded = subtitleDecoder.TryDecode(packet, out cues);
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }

        if (!decoded.IsSuccessful)
        {
            return decoded;
        }

        if (cues is null)
        {
            return OperationResult.Failure("The subtitle decoder returned no cues.");
        }

        CueWindow.Add(epoch, cues, timeline.Position, discardStale: !Renderer.HasComponent);
        if (cues.Count > 0)
        {
            RenderNode.Wake();
        }

        return OperationResult.Success();
    }

    public void Render(Func<bool> stopRequested)
    {
        void ReportOnFailure(string message, OperationResult result)
        {
            if (!result.IsSuccessful)
            {
                events.PostWarning($"{message}: {result.Message}");
            }
        }

        bool drawnInvalidated = true;
        long workingEpoch = -1;
        long drawnSurfaceVersion = -1;
        TimeSpan drawnPosition = TimeSpan.MinValue;
        SubtitlePresentation drawnPresentation = default;
        SubtitleCueSnapshot drawnCues = SubtitleCueSnapshot.Empty;

        while (true)
        {
            long stamp = RenderNode.Stamp;
            if (stopRequested())
            {
                break;
            }

            if (Renderer.Update(out ISubtitleRenderer? oldRenderer))
            {
                if (oldRenderer is not null)
                {
                    if (Renderer.CurrentComponent is null)
                    {
                        ReportOnFailure("The subtitles could not be cleared",
                            PipelineEvents.SafeInvoke(() => oldRenderer.Show([], TimeSpan.Zero, SubtitlePresentation.Identity)));
                    }

                    oldRenderer.Dispose();
                }

                drawnInvalidated = true;
                continue;
            }

            ISubtitleRenderer? renderer = Renderer.CurrentComponent;
            if (renderer is null)
            {
                RenderNode.Wait(stamp);
                continue;
            }

            if (!events.CheckState(out MediaEngineState state, MediaEngineState.Playing, MediaEngineState.Paused))
            {
                RenderNode.Wait(stamp);
                continue;
            }

            long epoch = timeline.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;
                drawnInvalidated = true;
            }

            TimeSpan position = timeline.Position;
            SubtitleCueSnapshot cues = CueWindow.GetVisibleCues(epoch, position);
            long surfaceVersion = Interlocked.Read(ref SurfaceVersion);

            SubtitlePresentation presentation = Presentation.Read() with
            {
                Frame = LayoutSource?.Frame ?? SubtitleFrame.Full
            };

            if (drawnInvalidated ||
                surfaceVersion != drawnSurfaceVersion ||
                presentation != drawnPresentation ||
                cues.Cues.Any(cue => cue.HasChanges(drawnPosition, position)) ||
                !SameCues(cues, drawnCues))
            {
                ReportOnFailure("The subtitles could not be drawn",
                    PipelineEvents.SafeInvoke(() => renderer.Show(cues.Cues, position, presentation)));
                drawnCues.Dispose();
                drawnCues = cues;
                drawnInvalidated = false;
                drawnSurfaceVersion = surfaceVersion;
                drawnPosition = position;
                drawnPresentation = presentation;
            }
            else
            {
                cues.Dispose();
            }

            int timeout = Timeout.Infinite;
            if (state == MediaEngineState.Playing)
            {
                TimeSpan? next = CueWindow.TimeUntilNextChange(position);
                if (next.HasValue)
                {
                    timeout = next.Value <= TimeSpan.Zero
                        ? SubtitleChangeIntervalMilliseconds
                        : (int)Math.Clamp(Math.Ceiling(next.Value.TotalMilliseconds), 1, int.MaxValue);
                }
            }

            RenderNode.Wait(stamp, timeout);
        }

        drawnCues.Dispose();
    }

    private static bool SameCues(SubtitleCueSnapshot x, SubtitleCueSnapshot y)
    {
        IReadOnlyList<SubtitleCue> xCues = x.Cues;
        IReadOnlyList<SubtitleCue> yCues = y.Cues;
        if (xCues.Count != yCues.Count)
        {
            return false;
        }

        for (int index = 0; index < xCues.Count; index++)
        {
            if (!ReferenceEquals(xCues[index], yCues[index]))
            {
                return false;
            }
        }

        return true;
    }
}
