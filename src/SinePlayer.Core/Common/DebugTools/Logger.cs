// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Text;

using SinePlayer.Core.Common.Storage;
using SinePlayer.Core.Common.Threading;

namespace SinePlayer.Core.Common.DebugTools;

public static class Logger
{
    private const string TAG = "Logger";
    private const int LEVEL_CONSOLE = 0;
    private const int LEVEL_DEBUG = 1;
    private const int LEVEL_INFO = 2;
    private const int LEVEL_WARN = 3;
    private const int LEVEL_ERROR = 4;
    private const int LEVEL_FATAL = 5;
    private const int FLUSH_INTERVAL = 5000;
    private const int BUFFER_SIZE = 1000;
    private const string EMPTY_TAG = "Empty";

    private static int sNextLogId = 0;
    private static int sPostDispatch = 0;
    private static readonly ConcurrentQueue<LogItem> sPendingQueue = new();
    private static readonly LinkedList<LogItem> sBuffer = new();
    private static readonly ConcurrentDictionary<ILogListener, LogListenerWrapper> sListeners = [];

    private static int sInitialized = 0;
    private static string sLogFolderPath = "";
    private static readonly ConcurrentQueue<LogItem> sFlushQueue = new();
    private static long sLastErrorReportTime = 0;

    private static bool Initialized => sInitialized == 1;

    public static void Initialize()
    {
        if (Interlocked.CompareExchange(ref sInitialized, 1, 0) != 0)
        {
            F(TAG, "Multiple initializations");
            return;
        }

        sLogFolderPath = StorageLocation.LocalCacheFolderPath + "\\Logs\\";

        Thread logThread = new(LogThreadMain)
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
        };

        logThread.Start();
        AddListener(new InternalLogListener());
    }

    public static void AddListener(ILogListener listener)
    {
        if (listener == null)
        {
            return;
        }

        LogListenerWrapper wrapper = new()
        {
            Listener = listener,
        };

        if (sListeners.TryAdd(listener, wrapper))
        {
            DispatchLogItems();
        }
    }

    public static void RemoveListener(ILogListener listener)
    {
        if (listener == null)
        {
            return;
        }

        sListeners.TryRemove(listener, out _);
    }

    public static void Flush()
    {
        if (!Initialized)
        {
            return;
        }

        FlushToFile();
    }

    public static bool IsTagMatch(string filter, string tag)
    {
        if (tag.Equals(filter, StringComparison.Ordinal))
        {
            return true;
        }

        return tag.Length > filter.Length
            && tag[filter.Length] == '/'
            && tag.StartsWith(filter, StringComparison.Ordinal);
    }

    public static string[] GetLogTreeFilePaths(string? tag)
    {
        if (!Initialized)
        {
            return [];
        }

        tag ??= EMPTY_TAG;
        string folderPath = Path.Combine(GetLogTreeFolder(), tag.Replace('/', '\\'));
        if (!Directory.Exists(folderPath))
        {
            return [];
        }

        string[] filePaths = Directory.GetFiles(folderPath, "log_*.txt");
        Array.Sort(filePaths, StringComparer.Ordinal);
        return filePaths;
    }

    public static void D(string? tag, string? message)
    {
        Log(LEVEL_DEBUG, tag, message, null);
    }

    public static void I(string? tag, string? message)
    {
        Log(LEVEL_INFO, tag, message, null);
    }

    public static void W(string? tag, string? message)
    {
        Log(LEVEL_WARN, tag, message, null);
    }

    public static void E(string? tag, string? message)
    {
        Log(LEVEL_ERROR, tag, message, null);
    }

    public static void E(string? tag, Exception? exception)
    {
        Log(LEVEL_ERROR, tag, null, exception);
    }

    public static void E(string? tag, string? message, Exception? exception)
    {
        Log(LEVEL_ERROR, tag, message, exception);
    }

    public static void F(string? tag, string? message)
    {
        AssertException exceptionNotNull = new(message);
        Log(LEVEL_FATAL, tag, message, exceptionNotNull);
    }

    public static void F(string? tag, Exception? exception)
    {
        AssertException exceptionNotNull = new(null, exception);
        Log(LEVEL_FATAL, tag, null, exceptionNotNull);
    }

    public static void F(string? tag, string? message, Exception? exception)
    {
        AssertException exceptionNotNull = new(message, exception);
        Log(LEVEL_FATAL, tag, message, exceptionNotNull);
    }

    public static void Assert(bool condition, string? message)
    {
        if (condition)
        {
            return;
        }

        AssertNotReachHereInternal(message, null);
    }

    public static void AssertNotReachHere(string? message)
    {
        AssertNotReachHereInternal(message, null);
    }

    private static void AssertNotReachHereInternal(string? message, Exception? exception)
    {
        AssertException exceptionNotNull = new(message, exception);
        Log(LEVEL_FATAL, "Assert", message, exceptionNotNull);
    }

    private static void Console(string message)
    {
        Log(LEVEL_CONSOLE, TAG, message, null);
    }

    private static void LogThreadMain()
    {
        Console("Logger initialized");

        while (true)
        {
            FlushToFile();
            Thread.Sleep(FLUSH_INTERVAL);
        }
    }

    private static void Log(int level, string? tag, string? message, Exception? exception)
    {
        tag ??= EMPTY_TAG;
        var item = new LogItem
        {
            Id = Interlocked.Increment(ref sNextLogId) - 1,
            Time = DateTimeOffset.Now,
            Level = level,
            Tag = tag,
            Message = message,
            Exception = exception
        };

        sPendingQueue.Enqueue(item);
        DispatchLogItems();

        if (item.Level >= LEVEL_FATAL)
        {
            if (item.Exception is AssertException assertException)
            {
                FailOnDebug(assertException);
            }
            else
            {
                FailOnDebug(new AssertException("Expect an AssertException.", item.Exception));
            }
        }
    }

    private static void DispatchLogItems()
    {
        bool postDispatch = Interlocked.Exchange(ref sPostDispatch, 1) == 0;
        if (!postDispatch)
        {
            return;
        }

        TaskDispatcher.DefaultQueue.Submit(() =>
        {
            Interlocked.Exchange(ref sPostDispatch, 0);
            List<LogItem> pendingLogs = [];
            while (sPendingQueue.TryDequeue(out LogItem? item))
            {
                pendingLogs.Add(item);
            }

            foreach (LogItem item in pendingLogs)
            {
                sBuffer.AddLast(item);
            }

            List<LogListenerWrapper> snapshot = [.. sListeners.Values];
            List<LogItem> dispatchingItems = [];
            foreach (LogListenerWrapper listener in snapshot)
            {
                dispatchingItems.Clear();
                for (LinkedListNode<LogItem>? node = sBuffer.Last; node != null; node = node.Previous)
                {
                    LogItem item = node.Value;
                    if (item.Id <= listener.LastId)
                    {
                        break;
                    }

                    dispatchingItems.Add(item);
                }

                for (int i = dispatchingItems.Count - 1; i >= 0; i--)
                {
                    LogItem item = dispatchingItems[i];
                    listener.LastId = item.Id;
                    listener.Listener.OnLog(item);
                }
            }

            while (sBuffer.Count > BUFFER_SIZE)
            {
                sBuffer.RemoveFirst();
            }
        });
    }

    private static void FlushToFile()
    {
        if (sFlushQueue.IsEmpty)
        {
            return;
        }

        List<LogItem> logs = [];
        while (true)
        {
            if (sFlushQueue.TryDequeue(out LogItem? item))
            {
                logs.Add(item);
            }
            else
            {
                break;
            }
        }

        FlushToLogFile(logs);
        FlushToLogTree(logs);
    }

    private static void FlushToLogFile(List<LogItem> logs)
    {
        StringBuilder sb = new();
        foreach (LogItem item in logs)
        {
            sb.Append(item.DisplayMessage);
            sb.Append('\n');
        }

        string content = sb.ToString();

        string fileName = "log_" + DateTime.Now.ToString("yyyyMMdd") + ".txt";
        string filePath = sLogFolderPath + fileName;
        try
        {
            Directory.CreateDirectory(sLogFolderPath);
            using StreamWriter writer = new(filePath, true, Encoding.UTF8);
            writer.Write(content);
            Console($"flushed {logs.Count} logs to {filePath}");
        }
        catch (Exception ex)
        {
            F(TAG, ex);
        }
    }

    private static void FlushToLogTree(List<LogItem> logs)
    {
        Dictionary<string, List<LogItem>> fileLogs = [];
        foreach (LogItem item in logs)
        {
            if (!DebugModel.IsLogTreeWhitelisted(item.Tag))
            {
                continue;
            }

            string[] categories = item.Tag.Split('/');
            bool divider = false;
            StringBuilder sb = new();
            foreach (string category in categories)
            {
                if (divider)
                {
                    sb.Append('\\');
                }
                divider = true;
                sb.Append(category);
                string path = sb.ToString();
                if (!fileLogs.TryGetValue(path, out List<LogItem>? logItems))
                {
                    logItems = [];
                    fileLogs[path] = logItems;
                }
                logItems.Add(item);
            }
        }

        string cacheFolder = GetLogTreeFolder();
        string fileName = "log_" + DateTime.Now.ToString("yyyyMMdd") + ".txt";

        foreach (KeyValuePair<string, List<LogItem>> pair in fileLogs)
        {
            StringBuilder sb = new();
            foreach (LogItem item in pair.Value)
            {
                sb.Append(item.DisplayMessage);
                sb.Append('\n');
            }
            string content = sb.ToString();

            string folderPath = cacheFolder + pair.Key;
            string filePath = $"{folderPath}\\{fileName}";
            try
            {
                Directory.CreateDirectory(folderPath);
                using StreamWriter writer = new(filePath, true, Encoding.UTF8);
                writer.Write(content);
                Console($"flushed {pair.Value.Count} logs to {filePath}");
            }
            catch (Exception ex)
            {
                F(TAG, ex);
                break;
            }
        }
    }

    private static void LogToConsole(string message)
    {
        System.Diagnostics.Debug.Print(message);
    }

    private static void FailOnDebug(AssertException exception)
    {
        long time = GetTick();
        if (time - sLastErrorReportTime > 5000)
        {
            sLastErrorReportTime = time;
            SentryManager.CaptureWarning(exception);
        }

        if (DebugUtils.DebugMode)
        {
            CrashHandler.OnCrash(exception);
        }
    }

    private static string GetLogTreeFolder()
    {
        return sLogFolderPath + "Tree\\";
    }

    private static long GetTick()
    {
        return Environment.TickCount64;
    }

    private class InternalLogListener : ILogListener
    {
        public void OnLog(LogItem item)
        {
            if (DebugModel.ConsoleEnabled)
            {
                LogToConsole(item.DisplayMessage);
            }

            if (DebugUtils.DebugMode && item.Level >= LEVEL_INFO)
            {
                sFlushQueue.Enqueue(item);
            }
        }
    }

    public class LogItem
    {
        public required int Id { init; get; }
        public required DateTimeOffset Time { init; get; }
        public required int Level { init; get; }
        public required string Tag { init; get; }
        public required string? Message { init; get; }
        public required Exception? Exception { init; get; }

        private string? _displayMessage = null;
        public string DisplayMessage
        {
            get
            {
                string? msg = _displayMessage;
                if (msg == null)
                {
                    msg = GenerateDisplayMessage();
                    _displayMessage = msg;
                }

                return msg;
            }
        }

        private string GenerateDisplayMessage()
        {
            string levelTag = Level switch
            {
                LEVEL_CONSOLE => "C",
                LEVEL_DEBUG => "D",
                LEVEL_INFO => "I",
                LEVEL_WARN => "W",
                LEVEL_ERROR => "E",
                LEVEL_FATAL => "F",
                _ => "U",
            };
            string realMessage = $"{Time:yyyy/M/d HH:mm:ss.fff} [{levelTag},{Tag}] {Message}";
            if (Exception != null)
            {
                realMessage += "\n" + Exception.ToString();
            }

            return realMessage;
        }
    }

    private class AssertException : Exception
    {
        public AssertException(string? message) : base(ConvertMessage(message)) { }

        public AssertException(string? message, Exception? inner) : base(ConvertMessage(message), inner) { }

        private static string ConvertMessage(string? message)
        {
            return message ?? "(no message)";
        }
    }

    private class LogListenerWrapper
    {
        public int LastId = -1;
        public required ILogListener Listener { init; get; }
    }

    public interface ILogListener
    {
        void OnLog(LogItem item);
    }
}
