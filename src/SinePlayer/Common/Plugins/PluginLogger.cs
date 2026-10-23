// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.SDK.Plugins;

namespace SinePlayer.Common.Plugins;

internal class PluginLogger(string pluginName) : ILogger
{
    private const string TAG = "Plugins";

    public void D(string tag, string? message)
    {
        Logger.D(Compose(tag), message);
    }

    public void I(string tag, string? message)
    {
        Logger.I(Compose(tag), message);
    }

    public void W(string tag, string? message)
    {
        Logger.W(Compose(tag), message);
    }

    public void E(string tag, string? message, Exception? exception)
    {
        Logger.E(Compose(tag), message, exception);
    }

    public void F(string tag, string? message, Exception? exception)
    {
        Logger.F(Compose(tag), message, exception);
    }

    private string Compose(string? tag)
    {
        return $"{TAG}/{pluginName}/{tag}";
    }
}
