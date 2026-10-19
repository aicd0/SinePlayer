// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Expression;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.AppEnvironment;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Database.SqlHelpers;
using SinePlayer.Data.Database;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Tables;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Helpers.Navigation;
using SinePlayer.Helpers.Search;
using SinePlayer.SDK.Models;
using SinePlayer.SDK.Plugins;
using SinePlayer.SDK.Plugins.Common;
using SinePlayer.SDK.Plugins.Media;
using SinePlayer.SDK.Plugins.Property;
using SinePlayer.SDK.Plugins.UI;
using SinePlayer.SDK.Plugins.UI.Menu;

namespace SinePlayer.Common.Plugins;

internal partial class PluginContext : IPluginContext
{
    private const string TAG = nameof(PluginContext);

    private IPlugin _plugin { get; init; }

    public PluginContext(IPlugin plugin, string pluginFilePath, PluginFileLoadContext loadContext)
    {
        Name = plugin.Name;
        Publisher = plugin.Publisher;
        Description = plugin.Description;
        PluginFilePath = pluginFilePath;
        LoadContext = loadContext;
        _plugin = plugin;
        _logger = new Lazy<ILogger>(() => new PluginLogger(Name));
        _registryDatabase = new Lazy<IRegistryDatabase>(() => AppDB.PluginRegistry(Name));
    }

    //
    // Public API
    //

    public delegate void StatusChangedEventHandler(PluginStatusEnum newStatus);
    public event StatusChangedEventHandler? StatusChanged;

    public string Name { get; init; }
    public string Publisher { get; init; }
    public string Description { get; init; }
    public IconSource? Icon => SafeAction(() => _plugin.Icon, null);
    public string Version => SafeAction(() => _plugin.Version, string.Empty);
    public string PluginFilePath { get; init; }
    public PluginFileLoadContext LoadContext { get; init; }
    public bool IsActive => Status == PluginStatusEnum.Initialized;

    private PluginStatusEnum _status = PluginStatusEnum.NotInitialized;
    public PluginStatusEnum Status
    {
        get => _status;
        private set
        {
            if (_status != value)
            {
                _status = value;
                StatusChanged?.Invoke(_status);
            }
        }
    }

    public void Initialize()
    {
        if (Status != PluginStatusEnum.NotInitialized)
        {
            Logger.F(TAG, $"({Name}) Initialize: Plugin already initialized or in error state");
            return;
        }

        if (SafeAction(() => _plugin.Initialize(this)))
        {
            Status = PluginStatusEnum.Initialized;
        }
    }

    public void DispatchMediaEditedEvent(IMediaModel media)
    {
        if (!IsActive)
        {
            return;
        }

        SafeAction(() => _mediaEditedEventHandler?.Invoke(media));
    }

    public IReadOnlyList<BaseMenuFlyoutItemModel> GetMainPageMoreMenuItems(IWindowContext windowContext)
    {
        if (!IsActive)
        {
            return [];
        }

        ICommonMenuItemCreator? creator = _mainPageMoreMenuItemCreator;
        if (creator is null)
        {
            return [];
        }

        return [.. SafeAction(() => creator.CreateMenuItems(windowContext), []).Select(CreateHostMenuFlyoutItem)];
    }

    public IReadOnlyList<BaseMenuFlyoutItemModel> GetMediaMenuItems(IWindowContext windowContext, IMediaModel primary, IEnumerable<IMediaModel> selection)
    {
        if (!IsActive)
        {
            return [];
        }

        IMediaMenuItemCreator? creator = _mediaMenuItemCreator;
        if (creator is null)
        {
            return [];
        }

        return [.. SafeAction(() => creator.CreateMenuItems(windowContext, primary, selection), []).Select(CreateHostMenuFlyoutItem)];
    }

    public IEnumerable<IVirtualProperty<IMediaModel>> GetAllMediaVirtualProperties()
    {
        if (!IsActive)
        {
            return [];
        }

        return [.. _mediaVirtualProperties.Values];
    }

    public IReadOnlyList<ISidebarPageProvider> GetAllSidebarPageProviders()
    {
        if (!IsActive)
        {
            return [];
        }

        return [.. _sidebarPageProviders];
    }

    public void SetForegroundMedia(PluginWindowContext windowContext, IMediaModel? media)
    {
        if (!IsActive)
        {
            return;
        }

        windowContext.SetForegroundMedia(media, SafeAction);
    }

    //
    // IPluginContext Implementation
    //

    private event MediaEditedEventHandler? _mediaEditedEventHandler;

    private readonly Dictionary<string, IVirtualProperty<IMediaModel>> _mediaVirtualProperties = [];
    private readonly List<ISidebarPageProvider> _sidebarPageProviders = [];
    private readonly Lazy<ILogger> _logger;
    private readonly Lazy<IRegistryDatabase> _registryDatabase;
    private IMediaMenuItemCreator? _mediaMenuItemCreator = null;
    private ICommonMenuItemCreator? _mainPageMoreMenuItemCreator = null;

    event MediaEditedEventHandler? IPluginContext.MediaEdited
    {
        add => _mediaEditedEventHandler += value;
        remove => _mediaEditedEventHandler -= value;
    }

    CultureInfo IPluginContext.CurrentCulture => EnvironmentProvider.Instance.GetCurrentAppLanguageInfo();

    string IPluginContext.PluginRootDirectoryPath => LoadContext.PluginRootDirectoryPath;

    ILogger IPluginContext.Logger => _logger.Value;

    IRegistryDatabase IPluginContext.RegistryDatabase => _registryDatabase.Value;

    IMediaMenuItemCreator? IPluginContext.MediaMenuItemCreator
    {
        get => _mediaMenuItemCreator;
        set => _mediaMenuItemCreator = value;
    }

    ICommonMenuItemCreator? IPluginContext.MainPageMoreMenuItemCreator
    {
        get => _mainPageMoreMenuItemCreator;
        set => _mainPageMoreMenuItemCreator = value;
    }

    Task IPluginContext.WithBusyState(Func<Task> action)
    {
        return BusyStateManager.WithBusyState(action);
    }

    Task<DialogResult> IPluginContext.EnqueueDialog(DialogOptions options)
    {
        return DialogUtils.EnqueueDialogAsync(options);
    }

    Task<DialogResult> IPluginContext.EnqueueDialog(ContentDialog dialog)
    {
        return DialogUtils.EnqueueDialogAsync(dialog);
    }

    async Task<IMediaModel?> IPluginContext.GetMedia(long id)
    {
        return await MediaModel.FromId(id);
    }

    async Task<IEnumerable<long>> IPluginContext.SearchMedia(string filterExpression)
    {
        ICondition? filterCondition = ParseFilterExpression(filterExpression) ?? throw new InvalidExpressionException();
        List<long> ids = [];
        await MediaHandle.Enqueue(() =>
        {
            var command = SelectCommand.Create(MediaTable.Instance);
            IReaderToken<long> idToken = command.PutQueryInt64(MediaTable.ColumnId);
            command.AppendCondition(filterCondition);
            command.AppendCondition(MediaHandle.CreateMediaOnlyCondition());
            using SelectCommand.IReader reader = command.Execute();
            while (reader.Read())
            {
                ids.Add(idToken.GetValue());
            }

            return true;
        });

        return ids;
    }

    void IPluginContext.RegisterPage(string host, Type pageType)
    {
        AppRouter.RegisterPage(host, pageType);
    }

    void IPluginContext.RegisterMediaVirtualProperty(IVirtualProperty<IMediaModel> property)
    {
        ArgumentNullException.ThrowIfNull(property, nameof(property));

        string name = property.Name;
        if (string.IsNullOrEmpty(name) || !VirtualPropertyNameRegex().IsMatch(name))
        {
            Logger.F(TAG, $"({Name}) RegisterMediaVirtualProperty: Invalid name '{name}'");
            return;
        }

        if (!_mediaVirtualProperties.TryAdd(name, property))
        {
            Logger.F(TAG, $"({Name}) RegisterMediaVirtualProperty: Property '{name}' already registered");
        }
    }

    void IPluginContext.RegisterSidebarPage(ISidebarPageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider, nameof(provider));
        _sidebarPageProviders.Add(provider);
    }

    //
    // Helpers
    //

    private bool SafeAction(Action? action)
    {
        if (action is null)
        {
            return true;
        }

        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Logger.F(TAG, $"Unhandled exception thrown from plugin: {Name}", ex);
            Status = PluginStatusEnum.Error;
            return false;
        }
    }

    private T SafeAction<T>(Func<T>? action, T defaultValue)
    {
        if (action is null)
        {
            return defaultValue;
        }

        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Logger.F(TAG, $"Unhandled exception thrown from plugin: {Name}", ex);
            Status = PluginStatusEnum.Error;
            return defaultValue;
        }
    }

    private BaseMenuFlyoutItemModel CreateHostMenuFlyoutItem(IMenuItem item)
    {
        return item switch
        {
            SimpleMenuItem simpleMenuItem => new SimpleMenuFlyoutItemModel()
            {
                Text = simpleMenuItem.Text,
                Icon = simpleMenuItem.Icon,
                IsEnabled = simpleMenuItem.IsEnabled,
                Click = () => SafeAction(simpleMenuItem.Click),
            },
            SeparatorMenuItem => new SeparatorMenuFlyoutItemModel(),
            ToggleMenuItem toggleMenuItem => new ToggleMenuFlyoutItemModel()
            {
                Text = toggleMenuItem.Text,
                IsChecked = toggleMenuItem.IsChecked,
                Click = () => SafeAction(toggleMenuItem.Click),
            },
            SubItemMenuItem subItemMenuItem => new SubItemMenuFlyoutItemModel()
            {
                Text = subItemMenuItem.Text,
                Icon = subItemMenuItem.Icon,
                Items = [.. subItemMenuItem.Items.Select(CreateHostMenuFlyoutItem)],
            },
            _ => throw new NotSupportedException($"Unsupported menu item type: {item.GetType().FullName}"),
        };
    }

    private static ICondition? ParseFilterExpression(string expression)
    {
        Common.Expression.Filter.ExpressionToken token;
        try
        {
            token = ExpressionParser.ParseFilter(expression);
        }
        catch (Exception ex)
        {
            Logger.E(TAG, ex);
            return null;
        }

        ICondition condition;
        try
        {
            condition = Common.Expression.Filter.Sql.SQLGenerator.CreateQuery(token, new MediaFilterSQLProvider());
        }
        catch (Exception ex)
        {
            Logger.E(TAG, ex);
            return null;
        }

        return condition;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_]+$")]
    private static partial Regex VirtualPropertyNameRegex();
}
