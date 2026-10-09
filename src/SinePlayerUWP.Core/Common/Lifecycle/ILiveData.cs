// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayerUWP.Core.Common.Lifecycle;

public interface ILiveData<T> : ILiveDataObserveAbility<IValueObserver<T>>, ILiveDataValueAbility<T>, ILiveDataCommonAbility where T : notnull
{
}
