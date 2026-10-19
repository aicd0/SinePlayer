// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

namespace SinePlayer.Common.BaseUI;

internal interface IPageTrait
{
    Type PageType { get; }

    bool IsImmersiveMode { get; }

    bool AllowMultiplePages { get; }
}
