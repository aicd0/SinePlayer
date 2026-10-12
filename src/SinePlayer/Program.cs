// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using SinePlayer.Common.InitTask;
using SinePlayer.Core.Common.DebugTools;

namespace SinePlayer;

public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            InitTaskManager.Instance.InitOnMain();
            Microsoft.UI.Xaml.Application.Start((p) =>
            {
                try
                {
                    var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                }
                catch (Exception ex)
                {
                    DebugUtils.CaptureFatalError("An unknown error occurred in ApplicationInitializationCallback.", ex, fastFail: true);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            DebugUtils.CaptureFatalError("An unknown error occurred in Program#Main.", ex, fastFail: true);
            throw;
        }
    }
}
