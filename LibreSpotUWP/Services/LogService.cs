using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace LibreSpotUWP.Services
{
    public static class LogService
    {
        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private static string _logPath;

        public static string LogPath => _logPath;

        public static async Task InitializeAsync()
        {
            _logPath = Path.Combine(ApplicationData.Current.TemporaryFolder.Path, "log.txt");
            await WriteAsync("INFO", "LogService", nameof(InitializeAsync), $"Logging initialized at {_logPath}");
        }

        public static void Info(string message, [CallerFilePath] string file = null, [CallerMemberName] string member = null)
            => _ = WriteAsync("INFO", GetClassName(file), member, message);

        public static void Warn(string message, [CallerFilePath] string file = null, [CallerMemberName] string member = null)
            => _ = WriteAsync("WARN", GetClassName(file), member, message);

        public static void Error(Exception ex, string message = null, [CallerFilePath] string file = null, [CallerMemberName] string member = null)
            => _ = WriteAsync("ERROR", GetClassName(file), member, $"{message ?? "Exception"} | {ex}");

        public static void Error(string message, [CallerFilePath] string file = null, [CallerMemberName] string member = null)
            => _ = WriteAsync("ERROR", GetClassName(file), member, message);

        private static async Task WriteAsync(string level, string className, string member, string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_logPath))
                    _logPath = Path.Combine(ApplicationData.Current.TemporaryFolder.Path, "log.txt");

                string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {className}.{member} {message}";
                Debug.WriteLine(line);

                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await File.AppendAllTextAsync(_logPath, line + Environment.NewLine).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch
            {
            }
        }

        private static string GetClassName(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? "Unknown" : Path.GetFileNameWithoutExtension(path);
        }
    }
}
