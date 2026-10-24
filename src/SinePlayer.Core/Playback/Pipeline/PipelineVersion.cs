// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Pipeline;

internal readonly record struct PipelineVersion(
    long Source,
    long AudioDecoder,
    long AudioRenderer,
    long VideoDecoder,
    long VideoRenderer)
{
    public static PipelineVersion Max(PipelineVersion x, PipelineVersion y) => new(
        Math.Max(x.Source, y.Source),
        Math.Max(x.AudioDecoder, y.AudioDecoder),
        Math.Max(x.AudioRenderer, y.AudioRenderer),
        Math.Max(x.VideoDecoder, y.VideoDecoder),
        Math.Max(x.VideoRenderer, y.VideoRenderer));

    public static VersionVerdict CompareVideoDecoder(PipelineVersion package, PipelineVersion self) => CombineVerdict(
        CompareUpstream(package.Source, self.Source),
        CompareOwn(package.VideoDecoder, self.VideoDecoder));

    public static VersionVerdict CompareVideoRenderer(PipelineVersion package, PipelineVersion self) => CombineVerdict(
        CompareUpstream(package.Source, self.Source),
        CompareUpstream(package.VideoDecoder, self.VideoDecoder),
        CompareOwn(package.VideoRenderer, self.VideoRenderer));

    public static VersionVerdict CompareAudioDecoder(PipelineVersion package, PipelineVersion self) => CombineVerdict(
        CompareUpstream(package.Source, self.Source),
        CompareOwn(package.AudioDecoder, self.AudioDecoder));

    public static VersionVerdict CompareAudioRenderer(PipelineVersion package, PipelineVersion self) => CombineVerdict(
        CompareUpstream(package.Source, self.Source),
        CompareUpstream(package.AudioDecoder, self.AudioDecoder),
        CompareOwn(package.AudioRenderer, self.AudioRenderer));

    private static VersionVerdict CompareUpstream(long package, long self)
    {
        if (package == 0 || package == self)
        {
            return VersionVerdict.Accept;
        }

        return package > self ? VersionVerdict.Block : VersionVerdict.Drop;
    }

    private static VersionVerdict CompareOwn(long package, long self)
    {
        return package > self ? VersionVerdict.Block : VersionVerdict.Accept;
    }

    private static VersionVerdict CombineVerdict(VersionVerdict first, VersionVerdict second)
    {
        return CombineVerdict(first, second, VersionVerdict.Accept);
    }

    private static VersionVerdict CombineVerdict(VersionVerdict first, VersionVerdict second, VersionVerdict third)
    {
        if (first == VersionVerdict.Drop || second == VersionVerdict.Drop || third == VersionVerdict.Drop)
        {
            return VersionVerdict.Drop;
        }

        return first == VersionVerdict.Block || second == VersionVerdict.Block || third == VersionVerdict.Block
            ? VersionVerdict.Block
            : VersionVerdict.Accept;
    }

    public long Highest => Math.Max(Source, Math.Max(AudioDecoder, Math.Max(AudioRenderer, Math.Max(VideoDecoder, VideoRenderer))));

    public long Next => Highest + 1;

    public PipelineVersion WithSource(long source) => this with { Source = source };

    public PipelineVersion WithAudio(long version) => this with { AudioDecoder = version, AudioRenderer = version };

    public PipelineVersion WithVideo(long version) => this with { VideoDecoder = version, VideoRenderer = version };
}

internal enum VersionVerdict
{
    Accept,
    Block,
    Drop,
}
