// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Common.Lifecycle;

public interface ILiveData<T> : ILiveDataObserveAbility<IValueObserver<T>>, ILiveDataValueAbility<T>, ILiveDataCommonAbility where T : notnull
{
}
