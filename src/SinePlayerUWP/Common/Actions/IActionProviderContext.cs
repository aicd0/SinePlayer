// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayerUWP.Common.Actions;

internal interface IActionProviderContext
{
    T? GetComponent<T>() where T : IActionComponent;
}
