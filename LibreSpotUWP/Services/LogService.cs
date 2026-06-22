using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace LibreSpotUWP.Services
{
    public static class LogService
    {
        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private static readonly Regex JsonSecretRegex = new Regex("(\"(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|authorization|auth[_-]?blob|audio[_-]?key|private[_-]?key|code)\"\\s*:\\s*\")[^\"]*(\")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex KeyValueSecretRegex = new Regex("\\b((?:access[_-]?token|refresh[_-]?token|client[_-]?secret|authorization|auth[_-]?blob|audio[_-]?key|private[_-]?key|code)\\s*[:=]\\s*)[^&\\s,;}\\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex BearerTokenRegex = new Regex("\\bBearer\\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex WindowsUserPathRegex = new Regex("C:\\\\Users\\\\[^\\\\]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SpotifyAuthenticatedAsRegex = new Regex("(Authenticated as ')[^']+(')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SpotifyConnectedUserRegex = new Regex("(Connected as user:\\s*)\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LeadingBracketPrefixRegex = new Regex("^\\[([^\\]]+)\\]\\s*", RegexOptions.CultureInvariant);
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

                string sanitizedMessage = EnsureSourcePrefix(className, member, Sanitize(message));
                string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {sanitizedMessage}";
                Trace.WriteLine(line);

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
