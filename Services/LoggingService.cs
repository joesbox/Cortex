using Avalonia.Threading;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cortex.Services
{
    public static class LoggingService
    {
        private static readonly object _lock = new();
        private static readonly object _fileLock = new();

        public static ObservableCollection<string> LogEntries { get; } = new();

        public static string DetailedLogDirectoryPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cortex",
            "Logs");

        public static string DetailedLogFilePath => Path.Combine(
            DetailedLogDirectoryPath,
            $"cortex-{DateTime.Now:yyyyMMdd}.log");

        public static void AddLog(
            string message,
            string? details = null,
            Exception? exception = null,
            [CallerMemberName] string callerMemberName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            if (message is null)
            {
                message = string.Empty;
            }

            DateTime now = DateTime.Now;
            string entry = $"[{now:HH:mm:ss}] {message}";
            string detailedEntry = BuildDetailedEntry(
                now,
                message,
                details,
                exception,
                callerMemberName,
                callerFilePath,
                callerLineNumber);

            WriteDetailedLog(detailedEntry);

            Dispatcher.UIThread.Post(() =>
            {
                lock (_lock)
                {
                    LogEntries.Insert(0, entry);
                }
            });
        }

        public static void Clear()
        {
            Dispatcher.UIThread.Post(() =>
            {
                lock (_lock)
                {
                    LogEntries.Clear();
                }
            });
        }

        private static string BuildDetailedEntry(
            DateTime timestamp,
            string message,
            string? details,
            Exception? exception,
            string callerMemberName,
            string callerFilePath,
            int callerLineNumber)
        {
            string sourceFileName = Path.GetFileName(callerFilePath);
            var builder = new StringBuilder();
            builder.Append($"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] [T{Environment.CurrentManagedThreadId}] ");
            builder.Append($"[{sourceFileName}:{callerLineNumber}::{callerMemberName}] ");
            builder.Append(message);

            if (!string.IsNullOrWhiteSpace(details))
            {
                builder.Append($" | details: {Sanitize(details)}");
            }

            if (exception != null)
            {
                builder.Append($" | exception: {Sanitize(exception.ToString())}");
            }

            return builder.ToString();
        }

        private static string Sanitize(string text)
        {
            return text.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static void WriteDetailedLog(string entry)
        {
            try
            {
                lock (_fileLock)
                {
                    Directory.CreateDirectory(DetailedLogDirectoryPath);
                    File.AppendAllText(DetailedLogFilePath, entry + Environment.NewLine);
                }
            }
            catch
            {
            }
        }
    }
}
