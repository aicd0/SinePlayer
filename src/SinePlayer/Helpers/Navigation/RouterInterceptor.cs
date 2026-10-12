// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Helpers.Navigation;

internal interface IRouterInterceptor
{
    bool Intercept(Route route, out PageNavigationBundle? navigationBundle);
}
