// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Localization;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.SDK.Models;

namespace SinePlayer.Common.BaseUI;

public partial class BaseContentDialog : ContentDialog, ILifecycleOwner
{
    private readonly SimpleLifecycleManager _lifecycleManager = new();

    private bool _isLoaded = false;

    public StringResourceProvider StringResource { get; } = StringResourceProvider.Instance;
    public int WindowId { get; set; }

    public BaseContentDialog()
    {
        _lifecycleManager.Initialize(GetType().Name, new LifecycleHandler(this));

        Loaded += OnLoadedInternal;
        Unloaded += OnUnloadedInternal;

        MemoryLeakTracker.TrackObject(this);
    }

    public ILifecycle GetLifecycle()
    {
        return _lifecycleManager.GetLifecycle();
    }

    public async Task<DialogResult> ShowAsync(int windowId)
    {
        return await DialogUtils.EnqueueDialogAsync(windowId, this);
    }

    protected virtual void OnStart()
    {
    }

    private void OnLoadedInternal(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _isLoaded = true;
        UpdateLifecycleState();
    }

    private void OnUnloadedInternal(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
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

        ILifecycle.State finalState = _isLoaded ? ILifecycle.State.Resumed : ILifecycle.State.Stopped;
        _lifecycleManager.SetState(finalState);
    }

    private class LifecycleHandler(BaseContentDialog dialog) : SimpleLifecycleManager.ILifecycleHandler
    {
        public void PreStart()
        {
        }

        public void PostStart()
        {
            dialog.OnStart();
        }

        public void PreResume()
        {
        }

        public void PostResume()
        {
        }

        public void PrePause()
        {
        }

        public void PostPause()
        {
        }

        public void PreStop()
        {
        }

        public void PostStop()
        {
        }
    }
}
