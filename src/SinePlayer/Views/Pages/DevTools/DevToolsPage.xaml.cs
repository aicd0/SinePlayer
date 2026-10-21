// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Actions.Providers;
using SinePlayer.Common.BaseUI;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Constants;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.Collections;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Database;
using SinePlayer.Helpers.Navigation;
using SinePlayer.SDK.Models;
using SinePlayer.ViewModels;

namespace SinePlayer.Views.Pages.DevTools;

internal sealed partial class DevToolsPage : BasePage
{
    private const string TAG = nameof(DevToolsPage);
    private const int MAX_LOG_ITEM_COUNT = 10000;
    private const int COUNTER_UPDATE_INTERVAL = 500;

    public DevToolsPage()
    {
        InitializeComponent();

        _logListener = new LogListener(this);
    }

    //
    // Lifecycle
    //

    protected override void OnStart(PageBundle bundle)
    {
        base.OnStart(bundle);

        GetMainPageAbility().SetTitle("Dev tools");
        GetMainPageAbility().SetIcon(new SymbolIconSource() { Symbol = Symbol.Repair });

        StartOrStopLogger(AppDB.MainRegistry.CreateKey(RegistryNames.STATE).GetValueOrDefault(RegistryNames.StateKey.LOG_STARTED, true));
        UpdateLogToggleButton();

        InitializeCounters();
        UpdateCounters();
    }

    protected override void OnResume()
    {
        base.OnResume();

        SetResult(null);
        RestoreConfig();
    }

    protected override void OnStop()
    {
        base.OnStop();

        Logger.RemoveListener(_logListener);
    }

    //
    // Events
    //

    private void ApplyConfigsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        string configs = CommonConfigsTextBlock.Text;
        try
        {
            DebugModel.SaveJsonConfig(configs);
        }
        catch (Exception ex)
        {
            SetResult(ex.ToString());
            return;
        }

        SetResult("Successfully applied");
        RestoreConfig();
    }

    private void RestoreConfigsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        RestoreConfig();
    }

    private void ResetConfigsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        DebugModel.SaveJsonConfig("null");
        RestoreConfig();
    }

    private void OpenPlaybackLabButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!DebugUtils.DeveloperMode)
        {
            return;
        }

        var route = Route.Create(RouterConstants.SCHEME_APP + RouterConstants.HOST_PLAYBACK_LAB);
        ActionModel actionModel = ActionModel.Builder.Create(OpenTabProvider.NAME)
            .AddParameter(OpenTabProvider.PARAM_URL, route.Url)
            .AddParameter(OpenTabProvider.PARAM_WINDOW_ID, "-1")
            .Build();
        PageActionHandler.HandleNoResult(actionModel);
    }

    private void CrashAppButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        throw new InvalidOperationException("Test");
    }

    private void TriggerBackgroundTaskFailure_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        TaskDispatcher.DefaultThreadPool.Submit(() =>
        {
            throw new InvalidOperationException("Test");
        });
    }

    private void TriggerAssertFailureButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Logger.AssertNotReachHere("MockAssertFailure");
    }

    private void ShowDialogOnActiveWindowButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            await Task.Delay(3000);
            DialogOptions options = new DialogOptions.Builder()
                .SetContent("This is a test dialog")
                .SetPrimaryButtonText("Primary")
                .SetSecondaryButtonText("Secondary")
                .Build();
            DialogResult result = await DialogUtils.EnqueueDialogAsync(options);
            SetResult($"Show dialog result: {result}");
        });
    }

    private void PrintMemoryLeakReportButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        SetResult(MemoryLeakTracker.GenerateReport());
    }

    private void RunGcBenchmarkButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        static string ToMB(long bytes)
        {
            return (bytes / 1048576.0).ToString("N1", CultureInfo.InvariantCulture);
        }

        var button = (Button)sender;
        button.IsEnabled = false;

        const int gcBenchmarkIterationCount = 5;

        var report = new StringBuilder();
        report.Append("GC.Collect(2, Forced, blocking: true) x ").Append(gcBenchmarkIterationCount).AppendLine();

        for (int i = 0; i < gcBenchmarkIterationCount; ++i)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            stopwatch.Stop();

            GCMemoryInfo memoryInfo = GC.GetGCMemoryInfo();
            report.Append('#').Append(i + 1)
                  .Append(": ").Append(stopwatch.Elapsed.TotalMilliseconds.ToString("N1", CultureInfo.InvariantCulture)).Append(" ms")
                  .Append(", gen=").Append(memoryInfo.Generation)
                  .Append(", heap=").Append(ToMB(memoryInfo.HeapSizeBytes)).Append(" MB")
                  .Append(", fragmented=").Append(ToMB(memoryInfo.FragmentedBytes)).Append(" MB")
                  .Append(", committed=").Append(ToMB(memoryInfo.TotalCommittedBytes)).Append(" MB")
                  .Append(", pause=").Append(memoryInfo.PauseTimePercentage.ToString("N2", CultureInfo.InvariantCulture)).Append('%')
                  .AppendLine();
        }

        button.IsEnabled = true;
        SetResult(report.ToString());
    }

    //
    // Logs
    //

    private readonly LogListener _logListener;
    private bool _logStarted = false;
    private bool _logStickToLatest = true;
    private readonly ObservableRingCollection<LogItemViewModel> _logItems = [];
    private readonly ConcurrentQueue<string> _pendingLogMessages = new();
    private int _logFlushScheduled = 0;

    public ObservableRingCollection<LogItemViewModel> LogItems => _logItems;

    private void LogToggleButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        StartOrStopLogger(!_logStarted);
    }

    private void LogStickButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _logStickToLatest = LogStickButton.IsChecked == true;
        if (_logStickToLatest)
        {
            ScrollToLatestLog();
        }
    }

    private void StartOrStopLogger(bool started)
    {
        if (started && !DebugUtils.DeveloperMode)
        {
            return;
        }

        if (started == _logStarted)
        {
            return;
        }

        AppDB.MainRegistry.CreateKey(RegistryNames.STATE).Set(RegistryNames.StateKey.LOG_STARTED, started);
        _logStarted = started;
        if (started)
        {
            Logger.AddListener(_logListener);
        }

        UpdateLogToggleButton();
    }

    private void UpdateLogToggleButton()
    {
        LogToggleButton.Content = _logStarted ? "Pause" : "Start";
    }

    private void ScrollToLatestLog()
    {
        if (_logItems.Count == 0)
        {
            return;
        }

        LogListView.ScrollIntoView(_logItems[^1]);
    }

    private void AppendLog(string message)
    {
        _pendingLogMessages.Enqueue(message);

        if (Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
        {
            CoroutineUtils.RunInMainThread(FlushPendingLogs);
        }
    }

    private void FlushPendingLogs()
    {
        Interlocked.Exchange(ref _logFlushScheduled, 0);

        LogItemViewModel? lastItem = null;
        while (_pendingLogMessages.TryDequeue(out string? message))
        {
            lastItem = new LogItemViewModel()
            {
                Text = message,
            };
            _logItems.Add(lastItem);
        }

        while (_logItems.Count > MAX_LOG_ITEM_COUNT)
        {
            _logItems.RemoveAt(0);
        }

        if (_logStickToLatest && lastItem != null)
        {
            LogListView.ScrollIntoView(lastItem);
        }
    }

    private class LogListener(DevToolsPage page) : Logger.ILogListener
    {
        public void OnLog(Logger.LogItem item)
        {
            Interlocked.Increment(ref page._receivedLogCount);
            page.RequestCounterUpdate();

            if (!page._logStarted)
            {
                return;
            }

            if (item.Level <= 4)
            {
                List<LogTag?> consoleWhitelist = DebugModel.ConsoleWhitelist;
                if (!consoleWhitelist.Any(t => t is null || t.ContainsAny(item.Tag)))
                {
                    return;
                }
            }

            page.AppendLog(item.DisplayMessage);
        }
    }

    //
    // Counters
    //

    private readonly List<EventCounter> _counters = [];
    private long _receivedLogCount = 0L;
    private long _lastCounterUpdateTime = 0L;
    private int _counterUpdateScheduled = 0;

    private void InitializeCounters()
    {
        GlobalEvent events = GlobalEvent.Instance;
        _counters.Add(new EventCounter("Logs", () => Interlocked.Read(ref _receivedLogCount)));
        ObserveEventCounter("CollectionUpdated", events.CollectionUpdated);
        ObserveEventCounter("MediaUpdated", events.MediaUpdated);
        ObserveEventCounter("FilterUpdated", events.FilterUpdated);
        ObserveEventCounter("FavoriteUpdated", events.FavoriteUpdated);
        ObserveEventCounter("HistoryUpdated", events.HistoryUpdated);
        ObserveEventCounter("TagInfoUpdated", events.TagInfoUpdated);
    }

    private void ObserveEventCounter<T>(string name, ILiveDataObserveAbility<IValueObserver<T>> liveData) where T : notnull
    {
        long count = 0L;
        _counters.Add(new EventCounter(name, () => count));
        liveData.Observe(this, _ =>
        {
            count++;
            RequestCounterUpdate();
        });
    }

    private void RequestCounterUpdate()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastCounterUpdateTime);
        if (now - last >= COUNTER_UPDATE_INTERVAL && Interlocked.CompareExchange(ref _lastCounterUpdateTime, now, last) == last)
        {
            CoroutineUtils.RunInMainThread(UpdateCounters);
            return;
        }

        if (Interlocked.CompareExchange(ref _counterUpdateScheduled, 1, 0) != 0)
        {
            return;
        }

        CoroutineUtils.Run(async () =>
        {
            int remaining = (int)(COUNTER_UPDATE_INTERVAL - (Environment.TickCount64 - Interlocked.Read(ref _lastCounterUpdateTime)));
            if (remaining > 0)
            {
                await Task.Delay(remaining);
            }

            Interlocked.Exchange(ref _counterUpdateScheduled, 0);
            Interlocked.Exchange(ref _lastCounterUpdateTime, Environment.TickCount64);
            CoroutineUtils.RunInMainThread(UpdateCounters);
        });
    }

    private void UpdateCounters()
    {
        var builder = new StringBuilder();
        foreach (EventCounter counter in _counters)
        {
            builder.Append((counter.Name + ": ").PadRight(20)).Append(counter.Count.ToString("N0", CultureInfo.InvariantCulture)).AppendLine();
        }

        CounterTextBlock.Text = builder.ToString().TrimEnd();
    }

    private class EventCounter(string name, Func<long> getCount)
    {
        public string Name { get; } = name;

        public long Count => getCount();
    }

    //
    // Utilities
    //

    private IMainPageAbilityForTab GetMainPageAbility()
    {
        return GetAbility<IMainPageAbilityForTab>()!;
    }

    private void SetResult(string? result)
    {
        if (result == null || result.Length == 0)
        {
            result = "Operation result shows here";
        }

        result = $"{DateTimeOffset.Now:yyyy/M/d HH:mm:ss.fff}\n{result.TrimEnd()}";
        TbOperationResult.Text = result;
    }

    private void RestoreConfig()
    {
        CommonConfigsTextBlock.Text = DebugModel.LoadJsonConfig();
    }
}
