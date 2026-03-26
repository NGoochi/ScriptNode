using System;
using System.IO;
using System.Threading;

using Rhino;

namespace ScriptNodePlugin
{
    /// <summary>
    /// Wraps FileSystemWatcher with debounce and UI-thread marshalling.
    /// </summary>
    public sealed class ScriptFileWatcher : IDisposable
    {
        private FileSystemWatcher _watcher;
        private Timer _debounceTimer;
        private readonly Action _onFileChanged;
        private readonly int _debounceMs;
        private string _watchedFilePath;
        private bool _disposed;

        /// <summary>
        /// The full path currently being watched.
        /// </summary>
        public string WatchedPath => _watchedFilePath;

        /// <summary>
        /// Create a new file watcher.
        /// </summary>
        /// <param name="onFileChanged">
        /// Callback invoked on the UI thread when the watched file changes (after debounce).
        /// </param>
        /// <param name="debounceMs">Debounce interval in milliseconds.</param>
        public ScriptFileWatcher(Action onFileChanged, int debounceMs = 150)
        {
            _onFileChanged = onFileChanged ?? throw new ArgumentNullException(nameof(onFileChanged));
            _debounceMs = debounceMs;
        }

        /// <summary>
        /// Start watching a specific file. If already watching a different file,
        /// the old watcher is disposed and a new one is created.
        /// </summary>
        public void Watch(string filePath)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(filePath)) return;

            var fullPath = Path.GetFullPath(filePath);

            // Already watching this exact file
            if (_watchedFilePath == fullPath && _watcher != null)
                return;

            // Tear down old watcher
            StopWatching();

            _watchedFilePath = fullPath;

            var directory = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return;

            try
            {
                _watcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _watcher.Changed += OnFileEvent;
                _watcher.Created += OnFileEvent;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"ScriptNode: Failed to create file watcher: {ex.Message}");
                _watcher?.Dispose();
                _watcher = null;
            }
        }

        /// <summary>
        /// Stop watching and release the FileSystemWatcher.
        /// </summary>
        public void StopWatching()
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;

            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileEvent;
                _watcher.Created -= OnFileEvent;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        /// <summary>
        /// Raw event handler — fires on a background thread. Resets the debounce timer.
        /// </summary>
        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            if (_disposed) return;

            // Reset or start the debounce timer
            if (_debounceTimer == null)
            {
                _debounceTimer = new Timer(OnDebounceElapsed, null, _debounceMs, Timeout.Infinite);
            }
            else
            {
                _debounceTimer.Change(_debounceMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Called after the debounce interval with no new events. Marshals to UI thread.
        /// </summary>
        private void OnDebounceElapsed(object state)
        {
            if (_disposed) return;

            try
            {
                RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    if (!_disposed)
                    {
                        _onFileChanged();
                    }
                }));
            }
            catch (Exception ex)
            {
                // Swallow — component may have been removed between timer fire and invoke
                System.Diagnostics.Debug.WriteLine($"ScriptNode watcher callback error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopWatching();
        }
    }
}
