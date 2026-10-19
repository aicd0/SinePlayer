// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Common.Actions;

internal interface IActionProviderContext
{
    T? GetComponent<T>() where T : IActionComponent;
}
