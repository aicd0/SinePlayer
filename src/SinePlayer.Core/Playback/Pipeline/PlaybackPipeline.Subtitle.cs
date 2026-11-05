// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Numerics;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed partial class PlaybackPipeline
{
    #region Subtitle decode

    private void TryWakeSubtitleDecode()
    {
        if (!_subtitlePacketQueue.IsEmpty)
        {
            _subtitleDecodeNode.Wake();
        }
    }

    private async Task SubtitleDecodeCore()
    {
        PacketQueue queue = _subtitlePacketQueue;
        ComponentSlot<ISubtitleDecoder> subtitleDecoderSlot = _subtitleDecoder;
        long workingEpoch = -1;

        while (true)
        {
            long stamp = _subtitleDecodeNode.Stamp;
            if (_stopRequested)
            {
                return;
            }

            // Install subtitle decoder
            if (subtitleDecoderSlot.Update(out ISubtitleDecoder? oldSubtitleDecoder))
            {
                oldSubtitleDecoder?.Dispose();
                _cueWindow.Reset();
            }

            // Bump epoch
            EpochGate epochGate = _timeline.Gate;
            long epoch = epochGate.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    subtitleDecoderSlot.CurrentComponent?.Flush();
                }
                catch (Exception ex)
                {
                    _events.PostWarning($"The subtitle decoder could not be flushed: {ex.Message}");
                }

                _cueWindow.SeekTo(epoch, epochGate.SeekPosition);
                continue;
            }

            if (!queue.TryPeek(out PacketEntry entry))
            {
                await _subtitleDecodeNode.WaitAsync(stamp);
                continue;
            }

            PipelineVersion subtitleDecoderVersions = subtitleDecoderSlot.CurrentVersions;
            VersionVerdict verdict = PipelineVersion.CompareSubtitleDecoder(entry.Versions, subtitleDecoderVersions);
            if (verdict == VersionVerdict.Block)
            {
                await _subtitleDecodeNode.WaitAsync(stamp);
                continue;
            }

            if (!queue.TryDequeue(out _))
            {
                await _subtitleDecodeNode.WaitAsync(stamp);
                continue;
            }

            TryWakeReader();

            MediaPacket? packet = entry.Packet;
            long packetEpoch = entry.Epoch;
            using (packet)
            {
                if (verdict == VersionVerdict.Drop || epoch != packetEpoch)
                {
                    continue;
                }

                ISubtitleDecoder? subtitleDecoder = subtitleDecoderSlot.CurrentComponent;
                if (packet is null || subtitleDecoder is null)
                {
                    continue;
                }

                OperationResult routed = DecodeSubtitle(epoch, subtitleDecoder, packet);
                if (!routed.IsSuccessful)
                {
                    _events.PostFailure(routed);
                    return;
                }
            }

            await Task.Yield();
        }
    }

    private OperationResult DecodeSubtitle(long epoch, ISubtitleDecoder subtitleDecoder, MediaPacket packet)
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

        _cueWindow.Add(epoch, cues, _clock.Position, discardStale: !_subtitleRenderer.HasComponent);
        if (cues.Count > 0)
        {
            _subtitleRenderNode.Wake();
        }

        return OperationResult.Success();
    }

    #endregion

    #region Subtitle render

    private void SubtitleRenderCore()
    {
        void ReportOnFailure(string message, OperationResult result)
        {
            if (!result.IsSuccessful)
            {
                _events.PostWarning($"{message}: {result.Message}");
            }
        }

        ComponentSlot<ISubtitleRenderer> subtitleRendererSlot = _subtitleRenderer;
        bool drawnInvalidated = true;
        long workingEpoch = -1;
        long drawnSurfaceVersion = -1;
        TimeSpan drawnPosition = TimeSpan.MinValue;
        SubtitlePresentation drawnPresentation = default;
        SubtitleCueSnapshot drawnCues = SubtitleCueSnapshot.Empty;

        while (true)
        {
            long stamp = _subtitleRenderNode.Stamp;
            if (_stopRequested)
            {
                break;
            }

            // Install subtitle renderer
            if (subtitleRendererSlot.Update(out ISubtitleRenderer? oldRenderer))
            {
                if (oldRenderer is not null)
                {
                    if (subtitleRendererSlot.CurrentComponent is null)
                    {
                        ReportOnFailure("The subtitles could not be cleared",
                            PipelineEvents.SafeInvoke(() => oldRenderer.Show([], TimeSpan.Zero, SubtitlePresentation.Identity)));
                    }

                    oldRenderer.Dispose();
                }

                drawnInvalidated = true;
                continue;
            }

            ISubtitleRenderer? renderer = subtitleRendererSlot.CurrentComponent;
            if (renderer is null)
            {
                _subtitleRenderNode.Wait(stamp);
                continue;
            }

            if (!CheckState(out MediaEngineState state, MediaEngineState.Playing, MediaEngineState.Paused))
            {
                _subtitleRenderNode.Wait(stamp);
                continue;
            }

            // Bump epoch
            EpochGate epochGate = _timeline.Gate;
            long epoch = epochGate.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;
                drawnInvalidated = true;
            }

            // Show
            TimeSpan position = _clock.Position;
            SubtitleCueSnapshot cues = _cueWindow.GetVisibleCues(epoch, position);
            long surfaceVersion = Interlocked.Read(ref _subtitleSurfaceVersion);

            SubtitlePresentation presentation = SubtitlePresentation with
            {
                Frame = ResolveSubtitleFrame()
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

            // Idle
            int timeout = Timeout.Infinite;
            if (state == MediaEngineState.Playing)
            {
                TimeSpan? next = _cueWindow.TimeUntilNextChange(position);
                if (next.HasValue)
                {
                    timeout = next.Value <= TimeSpan.Zero
                        ? SubtitleChangeIntervalMilliseconds
                        : (int)Math.Clamp(Math.Ceiling(next.Value.TotalMilliseconds), 1, int.MaxValue);
                }
            }

            _subtitleRenderNode.Wait(stamp, timeout);
        }

        drawnCues.Dispose();
    }

    private SubtitleFrame ResolveSubtitleFrame()
    {
        IVideoRenderTarget? videoTarget = _videoTarget;
        VideoStreamInfo? stream = _source.Latest?.Component?.VideoStream;
        if (videoTarget is null || stream is null || stream.Width <= 0 || stream.Height <= 0)
        {
            return SubtitleFrame.Full;
        }

        int surfaceWidth = videoTarget.SurfaceWidth;
        int surfaceHeight = videoTarget.SurfaceHeight;
        if (surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            return SubtitleFrame.Full;
        }

        // The placement maps the picture's own square onto the surface, so its four corners say where the picture ends:
        // the bounding box of that is the frame, whatever turning, zooming or mirroring was asked for.
        Matrix3x2 placement = _videoTransform.Read().GetPlacement(stream.Width, stream.Height, surfaceWidth, surfaceHeight);
        var zero = Vector2.Transform(Vector2.Zero, placement);
        var unitX = Vector2.Transform(Vector2.UnitX, placement);
        var unitY = Vector2.Transform(Vector2.UnitY, placement);
        var one = Vector2.Transform(Vector2.One, placement);

        float left = Math.Clamp(MathF.Min(MathF.Min(zero.X, unitX.X), MathF.Min(unitY.X, one.X)), 0.0f, 1.0f);
        float top = Math.Clamp(MathF.Min(MathF.Min(zero.Y, unitX.Y), MathF.Min(unitY.Y, one.Y)), 0.0f, 1.0f);
        float right = Math.Clamp(MathF.Max(MathF.Max(zero.X, unitX.X), MathF.Max(unitY.X, one.X)), 0.0f, 1.0f);
        float bottom = Math.Clamp(MathF.Max(MathF.Max(zero.Y, unitX.Y), MathF.Max(unitY.Y, one.Y)), 0.0f, 1.0f);

        // A picture larger than the surface is cropped by it, so what is left of it is the whole surface.
        return right > left && bottom > top
            ? new SubtitleFrame(left, top, right - left, bottom - top)
            : SubtitleFrame.Full;
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

    #endregion
}
