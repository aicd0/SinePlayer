// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Common.Imaging;
using SinePlayerUWP.UserControls.Reader.Imaging;

namespace SinePlayerUWP.UserControls.Reader;

internal class ReaderImageSource
{
    public required IImageSource Source { get; init; }
    public required ReaderImageSettings Settings { get; init; }
}
