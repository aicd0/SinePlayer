// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.Null;

internal sealed partial class NullSubtitleDecoder : ISubtitleDecoder
{
    private readonly SubtitleStreamInfo _stream;

    internal NullSubtitleDecoder(SubtitleStreamInfo stream)
    {
        _stream = stream;
    }

    public string Name => "Null";

    public MediaStreamInfo Stream => _stream;

    public bool IsHardwareAccelerated => false;

    /// <summary>Text, because a cue format is what a renderer is chosen by, and this one decodes whichever it is given.</summary>
    public SubtitleCueFormat CueFormat => SubtitleCueFormat.Ass;

    public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<SubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(packet);

        cues = [];
        return OperationResult.Success();
    }

    public OperationResult Drain(out IReadOnlyList<SubtitleCue> cues)
    {
        cues = [];
        return OperationResult.Success();
    }

    public OperationResult Flush()
    {
        return OperationResult.Success();
    }

    public void Dispose()
    {
    }
}
