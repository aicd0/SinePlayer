// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.Utils;

namespace SinePlayer.Core.Imaging;

public class LoadImageOptions
{
    public required CancellationSession.IToken Token { get; set; }
    public required IImageResultHandler Handler { get; set; }
    public double DecodeWidth { get; set; }
    public double DecodeHeight { get; set; }
    public ImageLoaderSchedulerGroup? SchedulerGroup { get; set; }
    public int Priority { get; set; } = 0;

    public LoadImageOptions Clone()
    {
        return new()
        {
            Token = Token,
            Handler = Handler,
            DecodeWidth = DecodeWidth,
            DecodeHeight = DecodeHeight,
            SchedulerGroup = SchedulerGroup,
            Priority = Priority,
        };
    }
}
