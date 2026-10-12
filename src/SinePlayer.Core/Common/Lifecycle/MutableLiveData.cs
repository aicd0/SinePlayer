// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Common.Lifecycle;

public class MutableLiveData<T> : LiveData<T>, IMutableLiveData<T> where T : notnull
{
    public MutableLiveData() : base() { }

    public MutableLiveData(T initialValue) : base(initialValue) { }

    public void Emit(T value)
    {
        EmitProtected(value);
    }
}
