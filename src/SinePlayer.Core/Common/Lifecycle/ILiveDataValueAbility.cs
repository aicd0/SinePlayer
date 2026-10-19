// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Common.Lifecycle;

public interface ILiveDataValueAbility<T> where T : notnull
{
    public bool HasValue { get; }

    public T? Value { get; }
}
