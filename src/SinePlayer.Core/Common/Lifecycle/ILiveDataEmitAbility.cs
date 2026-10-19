// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Common.Lifecycle;

public interface ILiveDataEmitAbility<T> where T : notnull
{
    void Emit(T value);
}
