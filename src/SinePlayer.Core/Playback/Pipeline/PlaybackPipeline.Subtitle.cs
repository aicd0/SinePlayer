// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Render;

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
                Frame = _subtitle.LayoutSource?.Frame ?? SubtitleFrame.Full
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
