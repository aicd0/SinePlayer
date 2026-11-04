// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ServiceManagement;
using SinePlayer.Core.Common.ServiceManagement.Services;

namespace SinePlayer.Core.Common.Storage;

public static class StorageLocation
{
    private static readonly Lazy<string> _runtimeIdentifier = new(() =>
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            Architecture arch => throw new NotSupportedException(
                $"This build has no native libraries for {arch}: they are provisioned for x64 and arm64 only."),
        };
    });
    private static string ProcessRuntimeIdentifier => _runtimeIdentifier.Value;

    private static readonly Lazy<string> _nativeLibraryFolderPath = new(() =>
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, "runtimes", ProcessRuntimeIdentifier, "native");
        return Directory.Exists(packaged) ? packaged : AppContext.BaseDirectory;
    });
    public static string NativeLibraryFolderPath => _nativeLibraryFolderPath.Value;

    private static readonly Lazy<string> _localFolderPath = new(() =>
    {
        return ServiceManager.GetService<IApplicationService>().GetLocalFolderPath();
    });
    public static string LocalFolderPath => _localFolderPath.Value;

    private static readonly Lazy<string> _localCacheFolderPath = new(() =>
    {
        return ServiceManager.GetService<IApplicationService>().GetLocalCacheFolderPath();
    });
    public static string LocalCacheFolderPath => _localCacheFolderPath.Value;

    private static readonly Lazy<string> _temporaryFolderPath = new(() =>
    {
        return ServiceManager.GetService<IApplicationService>().GetTemporaryFolderPath();
    });
    public static string TemporaryFolderPath => _temporaryFolderPath.Value;

    public static string RegistryFolderPath => Path.Combine(LocalFolderPath, "Registry");
}
