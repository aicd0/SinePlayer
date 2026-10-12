// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

using Microsoft.UI.Xaml.Controls;

using SinePlayer.SDK.Models;
using SinePlayer.SDK.Plugins.Media;
using SinePlayer.SDK.Plugins.Property;
using SinePlayer.SDK.Plugins.UI;
using SinePlayer.SDK.Plugins.UI.Menu;

namespace SinePlayer.SDK.Plugins;

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
