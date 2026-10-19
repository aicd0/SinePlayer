// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;

using Microsoft.UI.Xaml.Markup;

using SinePlayer.SDK.Plugins;

namespace SinePlayer.Common.Plugins;

internal class PluginFileLoadContext
{
    public required PluginAssemblyLoader AssemblyLoader { get; init; }
    public IReadOnlyCollection<IPlugin> Plugins { get; init; } = [];
    public IReadOnlyCollection<IXamlMetadataProvider> XamlMetadataProviders { get; init; } = [];
    public string PluginRootDirectoryPath { get; init; } = string.Empty;
    public required IReadOnlyDictionary<string, string> Assemblies { get; init; }
}
