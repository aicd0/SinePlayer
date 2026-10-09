// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Common.Localization;

namespace SinePlayerUWP.Common.BaseUI;

public class BaseViewModel
{
    public StringResourceProvider StringResource { get; } = StringResourceProvider.Instance;

    public virtual void NotifyImmediately()
    {
    }
}
