// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Engine;

internal readonly record struct ChainVersion(long Source, long Decoder, long Renderer)
{
    public static ChainVersion Max(ChainVersion x, ChainVersion y) => new(
        Math.Max(x.Source, y.Source),
        Math.Max(x.Decoder, y.Decoder),
        Math.Max(x.Renderer, y.Renderer));

    public static bool IsBehind(ChainVersion versions, ChainVersion reference) => versions.Source < reference.Source
        || versions.Decoder < reference.Decoder
        || versions.Renderer < reference.Renderer;

    public static VersionVerdict CompareDecoder(ChainVersion package, ChainVersion self) => CombineVerdict(
        CompareOwn(package.Decoder, self.Decoder),
        CompareUpstream(package.Source, self.Source));

    public static VersionVerdict CompareRenderer(ChainVersion package, ChainVersion self) => CombineVerdict(
        CompareOwn(package.Renderer, self.Renderer),
        CompareUpstream(package.Source, self.Source),
        CompareUpstream(package.Decoder, self.Decoder));

    private static VersionVerdict CompareOwn(long package, long self)
    {
        return package > self ? VersionVerdict.Block : VersionVerdict.Accept;
    }

    private static VersionVerdict CompareUpstream(long package, long self)
    {
        return package < self ? VersionVerdict.Drop : VersionVerdict.Accept;
    }

    private static VersionVerdict CombineVerdict(VersionVerdict first, VersionVerdict second)
    {
        return CombineVerdict(first, second, VersionVerdict.Accept);
    }

    private static VersionVerdict CombineVerdict(VersionVerdict first, VersionVerdict second, VersionVerdict third)
    {
        if (first == VersionVerdict.Block || second == VersionVerdict.Block || third == VersionVerdict.Block)
        {
            return VersionVerdict.Block;
        }

        return first == VersionVerdict.Drop || second == VersionVerdict.Drop || third == VersionVerdict.Drop
            ? VersionVerdict.Drop
            : VersionVerdict.Accept;
    }
}

internal enum VersionVerdict
{
    Accept,
    Block,
    Drop,
}
