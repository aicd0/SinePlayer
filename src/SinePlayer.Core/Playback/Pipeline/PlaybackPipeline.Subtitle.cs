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
        if (!_subtitle.PacketQueue.IsEmpty)
        {
            _subtitle.DecodeNode.Wake();
        }
    }

    private async Task SubtitleDecodeCore()
    {
        PacketQueue queue = _subtitle.PacketQueue;
        ComponentSlot<ISubtitleDecoder> subtitleDecoderSlot = _subtitle.Decoder;
        long workingEpoch = -1;

        while (true)
        {
            long stamp = _subtitle.DecodeNode.Stamp;
            if (_stopRequested)
            {
                return;
            }

            // Install subtitle decoder
            if (subtitleDecoderSlot.Update(out ISubtitleDecoder? oldSubtitleDecoder))
            {
                oldSubtitleDecoder?.Dispose();
                _subtitle.CueWindow.Reset();
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

                _subtitle.CueWindow.SeekTo(epoch, epochGate.SeekPosition);
                continue;
            }

            if (!queue.TryPeek(out PacketEntry entry))
            {
                await _subtitle.DecodeNode.WaitAsync(stamp);
                continue;
            }

            PipelineVersion subtitleDecoderVersions = subtitleDecoderSlot.CurrentVersions;
            VersionVerdict verdict = PipelineVersion.CompareSubtitleDecoder(entry.Versions, subtitleDecoderVersions);
            if (verdict == VersionVerdict.Block)
            {
                await _subtitle.DecodeNode.WaitAsync(stamp);
                continue;
            }

            if (!queue.TryDequeue(out _))
            {
                await _subtitle.DecodeNode.WaitAsync(stamp);
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

        _subtitle.CueWindow.Add(epoch, cues, _timeline.Position, discardStale: !_subtitle.Renderer.HasComponent);
        if (cues.Count > 0)
        {
            _subtitle.RenderNode.Wake();
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

        ComponentSlot<ISubtitleRenderer> subtitleRendererSlot = _subtitle.Renderer;
        bool drawnInvalidated = true;
        long workingEpoch = -1;
        long drawnSurfaceVersion = -1;
        TimeSpan drawnPosition = TimeSpan.MinValue;
        SubtitlePresentation drawnPresentation = default;
        SubtitleCueSnapshot drawnCues = SubtitleCueSnapshot.Empty;

        while (true)
        {
            long stamp = _subtitle.RenderNode.Stamp;
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
                _subtitle.RenderNode.Wait(stamp);
                continue;
            }

            if (!CheckState(out MediaEngineState state, MediaEngineState.Playing, MediaEngineState.Paused))
            {
                _subtitle.RenderNode.Wait(stamp);
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
            TimeSpan position = _timeline.Position;
            SubtitleCueSnapshot cues = _subtitle.CueWindow.GetVisibleCues(epoch, position);
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
                TimeSpan? next = _subtitle.CueWindow.TimeUntilNextChange(position);
                if (next.HasValue)
                {
                    timeout = next.Value <= TimeSpan.Zero
                        ? SubtitleChangeIntervalMilliseconds
                        : (int)Math.Clamp(Math.Ceiling(next.Value.TotalMilliseconds), 1, int.MaxValue);
                }
            }

            _subtitle.RenderNode.Wait(stamp, timeout);
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
