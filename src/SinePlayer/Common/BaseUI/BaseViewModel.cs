// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Common.Localization;

namespace SinePlayer.Common.BaseUI;

public class BaseViewModel
{
    public StringResourceProvider StringResource { get; } = StringResourceProvider.Instance;

    public virtual void NotifyImmediately()
    {
    }
}
