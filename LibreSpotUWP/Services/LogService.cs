using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace LibreSpotUWP.Services
{
    public static class LogService
    {
        private sealed class LogEntry
        {
            public string Level;
            public string ClassName;
            public string Member;
            public string Message;
            public DateTimeOffset Timestamp;
        }

        private const int MaximumQueuedEntries = 512;
        private const int MaximumBatchEntries = 64;
        private static readonly ConcurrentQueue<LogEntry> _queue = new ConcurrentQueue<LogEntry>();
        private static readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
        private static readonly object _writerLock = new object();
        private static readonly Regex JsonSecretRegex = new Regex("(\"(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|authorization|auth[_-]?blob|audio[_-]?key|private[_-]?key|code)\"\\s*:\\s*\")[^\"]*(\")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex KeyValueSecretRegex = new Regex("\\b((?:access[_-]?token|refresh[_-]?token|client[_-]?secret|authorization|auth[_-]?blob|audio[_-]?key|private[_-]?key|code)\\s*[:=]\\s*)[^&\\s,;}\\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex BearerTokenRegex = new Regex("\\bBearer\\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex WindowsUserPathRegex = new Regex("C:\\\\Users\\\\[^\\\\]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SpotifyAuthenticatedAsRegex = new Regex("(Authenticated as ')[^']+(')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SpotifyConnectedUserRegex = new Regex("(Connected as user:\\s*)\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LeadingBracketPrefixRegex = new Regex("^\\[([^\\]]+)\\]\\s*", RegexOptions.CultureInvariant);
        private static string _logPath;
        private static Task _writerTask;
        private static int _queuedEntries;
        private static long _droppedEntries;

        public static string LogPath => _logPath;

        public static Task InitializeAsync()
        {
            _logPath = Path.Combine(ApplicationData.Current.TemporaryFolder.Path, "log.txt");
            EnsureWriterStarted();
            Enqueue("INFO", "LogService", nameof(InitializeAsync), $"Logging initialized at {_logPath}");
            return Task.CompletedTask;
        }

        public static void Info(string message, [CallerFilePath] string file = null, [CallerMemberName] string member = null)
            => Enqueue("INFO", GetClassName(file), member, message);

        public static void Warn(string message, [CallerFilePath] string file = null, [CallerMemberName] string member = null)
            => Enqueue("WARN", GetClassName(file), member, message);

        public static void Error(Exception ex, string message = null, [CallerFilePath] string file = null, [CallerMemberName] string member = null)
            => Enqueue("ERROR", GetClassName(file), member, $"{message ?? "Exception"} | {ex}");

        public static void Error(string message, [CallerFilePath] string file = null, [CallerMemberName] string member = null)
            => Enqueue("ERROR", GetClassName(file), member, message);

        private static void Enqueue(string level, string className, string member, string message)
        {
            EnsureWriterStarted();
            if (Interlocked.Increment(ref _queuedEntries) > MaximumQueuedEntries)
            {
                Interlocked.Decrement(ref _queuedEntries);
                Interlocked.Increment(ref _droppedEntries);
                return;
            }

            _queue.Enqueue(new LogEntry
            {
                Level = level,
                ClassName = className,
                Member = member,
                Message = message,
                Timestamp = DateTimeOffset.Now
            });
            _queueSignal.Release();
        }

        private static void EnsureWriterStarted()
        {
            lock (_writerLock)
            {
                if (_writerTask == null)
                    _writerTask = Task.Run(ProcessQueueAsync);
            }
        }

        private static async Task ProcessQueueAsync()
        {
            while (true)
            {
                await _queueSignal.WaitAsync().ConfigureAwait(false);

                try
                {
                    if (string.IsNullOrWhiteSpace(_logPath))
                        _logPath = Path.Combine(ApplicationData.Current.TemporaryFolder.Path, "log.txt");

                    var batch = new StringBuilder();
                    int count = 0;
                    while (count < MaximumBatchEntries && _queue.TryDequeue(out LogEntry entry))
                    {
                        Interlocked.Decrement(ref _queuedEntries);
                        string sanitizedMessage = EnsureSourcePrefix(
                            entry.ClassName,
                            entry.Member,
                            Sanitize(entry.Message));
                        string line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{entry.Level}] {sanitizedMessage}";
                        Trace.WriteLine(line);
                        batch.AppendLine(line);
                        count++;
                    }

                    long dropped = Interlocked.Exchange(ref _droppedEntries, 0);
                    if (dropped > 0)
                    {
                        string droppedLine = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [WARN] [LogService.ProcessQueueAsync] Dropped {dropped} log entries because the bounded queue was full.";
                        Trace.WriteLine(droppedLine);
                        batch.AppendLine(droppedLine);
                    }

                    if (batch.Length > 0)
                        await File.AppendAllTextAsync(_logPath, batch.ToString()).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private static string GetClassName(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? "Unknown" : Path.GetFileNameWithoutExtension(path);
        }

        private static string EnsureSourcePrefix(string className, string member, string message)
        {
            var sourcePrefix = $"[{className}.{member}]";
            var sourceName = $"{className}.{member}";

            if (string.IsNullOrWhiteSpace(message))
                return sourcePrefix;

            message = NormalizeLeadingContextPrefix(sourceName, message);

            return $"{sourcePrefix} {message}";
        }

        private static string NormalizeLeadingContextPrefix(string sourceName, string message)
        {
            var match = LeadingBracketPrefixRegex.Match(message);
            if (!match.Success)
                return message;

            var tag = match.Groups[1].Value;
            var body = message.Substring(match.Length);

            if (string.Equals(tag, sourceName, StringComparison.Ordinal))
                return body;

            return string.IsNullOrWhiteSpace(body)
                ? $"context={tag}"
                : $"context={tag} {body}";
        }

        private static string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message;

            message = JsonSecretRegex.Replace(message, "$1[redacted]$2");
            message = KeyValueSecretRegex.Replace(message, "$1[redacted]");
            message = BearerTokenRegex.Replace(message, "Bearer [redacted]");
            message = WindowsUserPathRegex.Replace(message, "C:\\Users\\[user]");
            message = SpotifyAuthenticatedAsRegex.Replace(message, "$1[redacted]$2");
            message = SpotifyConnectedUserRegex.Replace(message, "$1[redacted]");
            return message;
        }
    }
}
