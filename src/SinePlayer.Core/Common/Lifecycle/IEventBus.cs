// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Common.Lifecycle;

public interface IEventBus
{
    public IMutableLiveData<T> With<T>(string eventId, Func<IMutableLiveData<T>> createFunc) where T : notnull;

    public void Clear();
}
