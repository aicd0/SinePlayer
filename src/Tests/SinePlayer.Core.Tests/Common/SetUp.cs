// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ServiceManagement;
using SinePlayer.Core.Common.ServiceManagement.Services;
using SinePlayer.Core.Common.Storage;

namespace SinePlayer.Core.Tests.Common;

internal static class SetUp
{
    public static void CommonSetUp()
    {
        // Register services
        ServiceManager.RegisterService<IApplicationService>(new ApplicationService());
        ServiceManager.RegisterService<IDebugService>(new DebugService());

        // Delete user data
        DeleteDirectory(StorageLocation.LocalFolderPath);
        DeleteDirectory(StorageLocation.LocalCacheFolderPath);
        DeleteDirectory(StorageLocation.TemporaryFolderPath);

        // Turn off debug mode
        DebugUtils.DebugMode = false;
        DebugUtils.DeveloperMode = false;

        // Initialize logger
        Logger.Initialize();
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
