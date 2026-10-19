// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Common.BaseUI;

internal interface IPageNavigationBundle
{
    public IPageTrait PageTrait { get; }

    public PageBundle Bundle { get; }

    public string Url { get; }

    public PageCommunicator Communicator { get; }
}
