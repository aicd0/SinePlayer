// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Actions.Components;
using SinePlayer.Common.Actions.Utils;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Localization;
using SinePlayer.Common.Plugins;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Lifecycle;

namespace SinePlayer.Common.BaseUI;

internal abstract class BasePage : Page, ILifecycleOwner
{
    private readonly SimpleLifecycleManager _lifecycleManager = new();
    private readonly PageLifecycleEventHandler _externalLifecycleHandler;
    private ILifecycle.State _externalLifecycleState = ILifecycle.State.Resumed;

    private bool _hasNavigatedTo = false;
    private bool _hasNavigatedFrom = false;
    private bool _isLoaded = false;
    private IPageNavigationBundle? _navigationBundle;

    protected int WindowId { get; private set; } = 0;
    public bool IsStarted => _lifecycleManager.GetLifecycle().GetState() >= ILifecycle.State.Started;
    public bool IsResumed => _lifecycleManager.GetLifecycle().GetState() >= ILifecycle.State.Resumed;

    public StringResourceProvider StringResource { get; } = StringResourceProvider.Instance;
    public ActionHandler PageActionHandler { get; } = new();

    public BasePage()
    {
        _externalLifecycleHandler = state =>
        {
            _externalLifecycleState = state;
            UpdateLifecycleState();
        };
        _lifecycleManager.Initialize(GetType().Name, new LifecycleHandler(this));

        Loaded += OnLoadedInternal;
        Unloaded += OnUnloadedInternal;

        MemoryLeakTracker.TrackObject(this);
    }

    public ILifecycle GetLifecycle()
    {
        return _lifecycleManager.GetLifecycle();
    }

    protected sealed override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
    }

    protected sealed override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        switch (e.NavigationMode)
        {
            case NavigationMode.New:
            case NavigationMode.Back:
            case NavigationMode.Forward:
                _navigationBundle = (IPageNavigationBundle)e.Parameter;
                _hasNavigatedTo = true;
                UpdateLifecycleState();
                break;
            case NavigationMode.Refresh:
                break;
        }
    }

    protected sealed override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        switch (e.NavigationMode)
        {
            case NavigationMode.New:
            case NavigationMode.Back:
            case NavigationMode.Forward:
                _hasNavigatedFrom = true;
                UpdateLifecycleState();
                break;
            case NavigationMode.Refresh:
                break;
        }
    }

    protected virtual void OnStart(PageBundle bundle)
    {
    }

    protected virtual void OnResume()
    {
    }

    protected virtual void OnPause()
    {
    }

    protected virtual void OnStop()
    {
    }

    protected T? GetAbility<T>() where T : class
    {
        return _navigationBundle!.Communicator.GetAbility<T>();
    }

    protected IEventBus GetWindowEventBus()
    {
        return App.Instance.WindowManager.GetEventBus(WindowId);
    }

    private void OnLoadedInternal(object sender, RoutedEventArgs e)
    {
        if (!((Page)sender).IsLoaded)
        {
            return;
        }

        _isLoaded = true;
        UpdateLifecycleState();
    }

    private void OnUnloadedInternal(object sender, RoutedEventArgs e)
    {
        if (((Page)sender).IsLoaded)
        {
            return;
        }

        _isLoaded = false;
        UpdateLifecycleState();
    }

    private void UpdateLifecycleState()
    {
        if (GetLifecycle().GetState() == ILifecycle.State.Stopped)
        {
            return;
        }

        ILifecycle.State finalState;
        if (_hasNavigatedFrom)
        {
            finalState = ILifecycle.State.Stopped;
        }
        else
        {
            finalState = _hasNavigatedTo ?
                (_isLoaded ? ILifecycle.State.Resumed : ILifecycle.State.Started) :
                ILifecycle.State.Initialized;
            finalState = (ILifecycle.State)Math.Min((int)_externalLifecycleState, (int)finalState);
        }

        _lifecycleManager.SetState(finalState);
    }

    private class LifecycleHandler(BasePage page) : SimpleLifecycleManager.ILifecycleHandler
    {
        public void PreStart()
        {
            IPageNavigationBundle bundle = page._navigationBundle!;

            // Retrieve window ID
            IMainWindowAbility? mainWindowAbility = page.GetAbility<IMainWindowAbility>() ?? throw new InvalidOperationException("IMainWindowAbility not found");
            int windowId = mainWindowAbility.WindowId;
            page.WindowId = windowId;
            page.PageActionHandler.RegisterComponent<IMainWindowComponent>(new MainWindowComponent(windowId, mainWindowAbility.PluginWindowContext));

            // Retrieve tab ID (if has)
            IMainPageAbilityForTab? mainPageAbility = page.GetAbility<IMainPageAbilityForTab>();
            if (mainPageAbility is not null)
            {
                page.PageActionHandler.RegisterComponent<IMainPageComponent>(new MainPageComponent(mainPageAbility.TabId));
            }

            ActionHandlerUtility.RegisterCommonProviders(page.PageActionHandler);
            page.GetAbility<ILifecycleAwareAbility>()!.RegisterPageLifecycleHandler(page._externalLifecycleHandler);
        }

        public void PostStart()
        {
            IPageNavigationBundle bundle = page._navigationBundle!;
            page.OnStart(bundle.Bundle);
        }

        public void PreResume()
        {
        }

        public void PostResume()
        {
            page.OnResume();
        }

        public void PrePause()
        {
        }

        public void PostPause()
        {
            page.OnPause();
        }

        public void PreStop()
        {
            page.GetAbility<ILifecycleAwareAbility>()!.UnregisterPageLifecycleHandler(page._externalLifecycleHandler);
        }

        public void PostStop()
        {
            page.OnStop();
        }
    }

    private class MainWindowComponent(int windowId, PluginWindowContext pluginWindowContext) : IMainWindowComponent
    {
        public int WindowId => windowId;

        public PluginWindowContext PluginWindowContext => pluginWindowContext;
    }

    private class MainPageComponent(string tabId) : IMainPageComponent
    {
        public string TabId => tabId;
    }
}
