// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

using SinePlayer.Common.InitTask;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.AppEnvironment;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Storage;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Helpers.Misc;
using SinePlayer.Helpers.Navigation;
using SinePlayer.Views.AppWindows.Main;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace SinePlayer;

public partial class App : Application
{
    private const string TAG = nameof(App);
    private const string COMMAND_LINE_FILE_NAME = "CommandLine.txt";

    private static App? sInstance;
    public static App Instance => sInstance!;

    public App()
    {
        try
        {
            sInstance = this;
            InitTaskManager.Instance.InitOnAppCreate(this);
            InitializeComponent();
        }
        catch (Exception ex)
        {
            DebugUtils.CaptureFatalError("Failed to initialize the application.", ex, fastFail: true);
            throw;
        }
    }

    //
    // Public API
    //

    internal WindowManager WindowManager { get; } = new();

    internal async Task OnCommandLine(MainWindow window, string[] args)
    {
        Route? route = await GetFileActivatedRoute(args);
        if (route is not null)
        {
            window.OpenTab(route.Url, string.Empty, string.Empty);
        }

        window.BringToFront();
    }

    //
    // Lifecycle
    //

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        try
        {
            OnLaunchedInternal(e);
        }
        catch (Exception ex)
        {
            DebugUtils.CaptureFatalError("An unknown error occurred in App#OnLaunched.", ex, fastFail: true);
            throw;
        }
    }

    private void OnLaunchedInternal(LaunchActivatedEventArgs e)
    {
        AppActivationArguments activatedEventArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        var mainInstance = AppInstance.FindOrRegisterForKey("main");
        bool isFirstInstance = InitTaskManager.Instance.IsFirstInstance;
        bool isMainInstance = mainInstance.IsCurrent;

        if (isMainInstance != isFirstInstance)
        {
            Logger.F(TAG, $"Inconsistent startup state: FirstInstance={isFirstInstance}, MainInstance={isMainInstance}");
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            return;
        }

        if (!isMainInstance)
        {
            if (EnvironmentProvider.IsPortable())
            {
                StoreCommandLine();
            }

            CoroutineUtils.Run(async () =>
            {
                await mainInstance.RedirectActivationToAsync(activatedEventArgs);
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            });

            return;
        }

        InitTaskManager.Instance.InitOnAppLaunch();

        mainInstance.Activated += (sender, e) =>
        {
            try
            {
                OnActivated(e, fromLaunch: false);
            }
            catch (Exception ex)
            {
                DebugUtils.CaptureFatalError("An unknown error occurred in App#Activated.", ex);
                throw;
            }
        };

        OnActivated(activatedEventArgs, fromLaunch: true);
    }

    private void OnActivated(AppActivationArguments e, bool fromLaunch)
    {
        string[] cmdArgs;
        if (EnvironmentProvider.IsPortable())
        {
            string? commandLine;
            if (fromLaunch)
            {
                commandLine = Environment.CommandLine;
            }
            else
            {
                commandLine = TryReadCommandLine();
                if (string.IsNullOrEmpty(commandLine))
                {
                    return;
                }
            }

            string[] splitedCommandLine = SplitCommandLine(commandLine);
            if (splitedCommandLine.Length >= 1)
            {
                cmdArgs = splitedCommandLine[1..];
            }
            else
            {
                cmdArgs = splitedCommandLine;
            }
        }
        else
        {
            switch (e.Kind)
            {
                case ExtendedActivationKind.File:
                    {
                        var fileArgs = (Windows.ApplicationModel.Activation.FileActivatedEventArgs)e.Data;
                        cmdArgs = [fileArgs.Files[0].Path];
                    }
                    break;
                default:
                    cmdArgs = [];
                    break;
            }
        }

        string cmd = string.Join(' ', cmdArgs);
        Logger.I(TAG, $"OnActivated: fromLaunch={fromLaunch}, cmd={cmd}");

        CoroutineUtils.RunInMainThreadAsync(async () =>
        {
            if (fromLaunch && !InitTaskManager.Instance.SafeMode)
            {
                WindowManager.RestoreWindowState();
            }

            MainWindow? window = WindowManager.GetAnyWindow();
            if (window is null)
            {
                Route? route = await GetFileActivatedRoute(cmdArgs);
                if (route is not null)
                {
                    MainWindow.Open(route.Url, restorePlacement: true);
                }
                else
                {
                    MainWindow.Open();
                }

                return;
            }

            await OnCommandLine(window, cmdArgs);
        });
    }

    private static void StoreCommandLine()
    {
        string commandLine = Environment.CommandLine;
        string temporaryFolderPath = StorageLocation.TemporaryFolderPath;
        if (!Directory.Exists(temporaryFolderPath))
        {
            Directory.CreateDirectory(temporaryFolderPath);
        }

        string commandLineFile = Path.Combine(temporaryFolderPath, COMMAND_LINE_FILE_NAME);
        File.WriteAllText(commandLineFile, commandLine);
    }

    private static string? TryReadCommandLine()
    {
        string temporaryFolderPath = StorageLocation.TemporaryFolderPath;
        if (!Directory.Exists(temporaryFolderPath))
        {
            return null;
        }

        string commandLineFile = Path.Combine(temporaryFolderPath, COMMAND_LINE_FILE_NAME);
        if (!File.Exists(commandLineFile))
        {
            return null;
        }

        string content;
        try
        {
            content = File.ReadAllText(commandLineFile);
        }
        catch (Exception ex)
        {
            Logger.F(TAG, nameof(TryReadCommandLine), ex);
            return null;
        }

        try
        {
            File.Delete(commandLineFile);
        }
        catch (Exception ex)
        {
            Logger.F(TAG, nameof(TryReadCommandLine), ex);
        }

        return content;
    }

    private static string[] SplitCommandLine(string commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return [];
        }

        unsafe
        {
            PWSTR* argv = PInvoke.CommandLineToArgv(commandLine, out int argc);
            if (argv == null)
            {
                return [];
            }

            try
            {
                string[] args = new string[argc];
                for (int i = 0; i < argc; i++)
                {
                    args[i] = argv[i].ToString();
                }

                return args;
            }
            finally
            {
                PInvoke.LocalFree((HLOCAL)argv);
            }
        }
    }

    //
    // File Activation
    //

    private static async Task<Route?> GetFileActivatedRoute(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        string targetFilePath = args[0];
        if (!File.Exists(targetFilePath))
        {
            Logger.W(nameof(GetFileActivatedRoute), $"Target file does not exist: {targetFilePath}");
            return null;
        }

        if (!PathUtils.TryNormalizePath(targetFilePath, out string? normalizedPath))
        {
            Logger.W(nameof(GetFileActivatedRoute), $"Failed to normalize file path: {targetFilePath}");
            return null;
        }

        targetFilePath = normalizedPath;
        string targetFileExtension = Path.GetExtension(targetFilePath);
        if (!AppInfoProvider.IsSupportedExternalFileExtension(targetFileExtension))
        {
            Logger.W(nameof(GetFileActivatedRoute), $"Unsupported file extension: {targetFileExtension}");
            return null;
        }

        MediaModel? media = await MediaModel.FromFile(targetFilePath);
        if (media is null)
        {
            Logger.W(nameof(GetFileActivatedRoute), $"Failed to create a media model from the file: {targetFilePath}");
            return null;
        }

        return OpenMediaHelper.GetMediaRoute(media);
    }
}
