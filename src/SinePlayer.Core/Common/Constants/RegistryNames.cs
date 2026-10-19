// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Common.Constants;

internal static class RegistryNames
{
    //
    // Core Registry
    //

    public const string GLOBAL = "/Global";
    public const string ENVIRONMENT_INFO = $"{GLOBAL}/EnvironmentInfo";
    public const string SETTINGS = $"{GLOBAL}/Settings";
    public const string DEBUG_SETTINGS = $"{SETTINGS}/Debug";
}
