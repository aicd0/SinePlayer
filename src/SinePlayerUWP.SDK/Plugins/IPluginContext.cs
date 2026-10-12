// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

using Microsoft.UI.Xaml.Controls;

using SinePlayerUWP.SDK.Models;
using SinePlayerUWP.SDK.Plugins.Media;
using SinePlayerUWP.SDK.Plugins.Property;
using SinePlayerUWP.SDK.Plugins.UI;
using SinePlayerUWP.SDK.Plugins.UI.Menu;

namespace SinePlayerUWP.SDK.Plugins;

public delegate void MediaEditedEventHandler(IMediaModel comic);

public interface IPluginContext
{
    event MediaEditedEventHandler? MediaEdited;

    CultureInfo CurrentCulture { get; }

    string PluginRootDirectoryPath { get; }

    ILogger Logger { get; }

    IRegistryDatabase RegistryDatabase { get; }

    IMediaMenuItemCreator? MediaMenuItemCreator { get; set; }

    ICommonMenuItemCreator? MainPageMoreMenuItemCreator { get; set; }

    Task WithBusyState(Func<Task> action);

    Task<DialogResult> EnqueueDialog(DialogOptions options);

    Task<DialogResult> EnqueueDialog(ContentDialog dialog);

    Task<IMediaModel?> GetMedia(long id);

    Task<IEnumerable<long>> SearchMedia(string filterExpression);

    void RegisterPage(string host, Type pageType);

    void RegisterMediaVirtualProperty(IVirtualProperty<IMediaModel> property);

    void RegisterSidebarPage(ISidebarPageProvider provider);
}
