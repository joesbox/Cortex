using Avalonia.Threading;
using System;
using System.Collections.ObjectModel;

namespace Cortex.Services
{
    public static class LoggingService
    {
        // Lock to guard operations (kept for clarity; updates are marshalled to UI thread)
        private static readonly object _lock = new();

        // Observable collection suitable for data-binding in the UI        
        public static ObservableCollection<string> LogEntries { get; } = new();

        /// <summary>
        /// Adds a timestamped message to the global log in a UI-thread-safe manner.
        /// Call from any thread.
        /// </summary>
        public static void AddLog(string message)
        {
            if (message is null) message = string.Empty;
            var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";

            // Ensure UI thread update for ObservableCollection
            Dispatcher.UIThread.Post(() =>
            {
                lock (_lock)
                {
                    LogEntries.Add(entry);
                }
            });
        }

        /// <summary>
        /// Clears the global log on the UI thread.
        /// </summary>
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
    }
}
