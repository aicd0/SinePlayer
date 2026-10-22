// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.IO;
using System.Text;

using SinePlayer.Common.InitTask;
using SinePlayer.Core.Common.AppEnvironment;
using SinePlayer.Core.Common.ServiceManagement.Services;
using SinePlayer.Data.Models.Misc;

using Windows.Storage;

namespace SinePlayer.Common.Services;

internal class ApplicationService : IApplicationService
{
    private const string TAG = nameof(ApplicationService);

#if PORTABLE
    private const bool PORTABLE = true;
#else
    private const bool PORTABLE = false;
#endif

    private const string DIR_USER_DATA = "UserData";

#pragma warning disable CS0162 // Unreachable code detected
    private static readonly Lazy<string> _localFolderPath = new(() =>
    {
        if (PORTABLE)
        {
            return Path.Combine(GetDeploymentPath(), DIR_USER_DATA, "Local");
        }
        else
        {
            return ApplicationData.Current.LocalFolder.Path;
        }
    });

    private static readonly Lazy<string> _localCacheFolderPath = new(() =>
    {
        if (PORTABLE)
        {
            return Path.Combine(GetDeploymentPath(), DIR_USER_DATA, "LocalCache");
        }
        else
        {
            return ApplicationData.Current.LocalCacheFolder.Path;
        }
    });

    private static readonly Lazy<string> _temporaryFolderPath = new(() =>
    {
        if (PORTABLE)
        {
            return Path.Combine(GetDeploymentPath(), DIR_USER_DATA, "Temporary");
        }
        else
        {
            return ApplicationData.Current.TemporaryFolder.Path;
        }
    });
#pragma warning restore CS0162 // Unreachable code detected

    private static bool _launching = true;
    private static bool _exiting = false;

    public static void StopLaunching()
    {
        _launching = false;
    }

    public static void StartExiting()
    {
        _launching = false;
        _exiting = true;
        App.Instance.WindowManager.LockWindowState();
        SidebarStateModel.Flush();
    }

    private static string GetDeploymentPath()
    {
        return AppContext.BaseDirectory;
    }

    public bool PortableBuild => PORTABLE;

    public bool SafeMode => InitTaskManager.Instance.SafeMode;

    public bool Launching => _launching;

    public bool Exiting => _exiting;

    public string GetLocalFolderPath()
    {
        return _localFolderPath.Value;
    }

    public string GetLocalCacheFolderPath()
    {
        return _localCacheFolderPath.Value;
    }

    public string GetTemporaryFolderPath()
    {
        return _temporaryFolderPath.Value;
    }

    public string GetEnvironmentDebugInfo()
    {
        StringBuilder sb = new();
        EnvironmentProvider.Instance.AppendDebugText(sb);
        return sb.ToString();
    }
}
