// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Null;

internal sealed partial class NullSubtitleRenderer : ISubtitleRenderer
{
    private readonly Lock _lock = new();

    private long _showCount;
    private string? _shownText;
    private SubtitlePresentation _placement;

    public string Name => "Null";

    public long ShowCount => Interlocked.Read(ref _showCount);

    public string? ShownText
    {
        get
        {
            lock (_lock)
            {
                return _shownText;
            }
        }
    }

    public SubtitlePresentation Presentation
    {
        get
        {
            lock (_lock)
            {
                return _placement;
            }
        }
    }

    public OperationResult Configure(SubtitleStreamInfo stream, IReadOnlyList<MediaAttachment> attachments, IVideoRenderTarget target)
    {
        return OperationResult.Success();
    }

    public OperationResult Show(IReadOnlyList<SubtitleCue> cues, TimeSpan position, SubtitlePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(cues);

        // The cues are the pipeline's and valid only for this call, so what is kept is a copy of the text.
        List<string> texts = [];
        foreach (SubtitleCue cue in cues)
        {
            texts.Add(cue.Text ?? string.Empty);
        }

        string? combined = texts.Count == 0 ? null : string.Join("\n", texts);
        lock (_lock)
        {
            _shownText = combined;
            _placement = presentation;
        }

        Interlocked.Increment(ref _showCount);
        return OperationResult.Success();
    }

    public void Dispose()
    {
    }
}
