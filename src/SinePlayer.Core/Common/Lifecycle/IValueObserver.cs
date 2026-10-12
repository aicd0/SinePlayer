// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Common.Lifecycle;

public interface IValueObserver<in T>
{
    void OnChanged(T value);
}
