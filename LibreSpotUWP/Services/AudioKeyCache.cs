using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LibreSpotUWP.Services
{
    public sealed class AudioKeyCache
    {
        private const string KeyExtension = ".key";
        private const byte PayloadVersion = 1;

        private readonly ConcurrentDictionary<string, byte[]> _hotCache = new ConcurrentDictionary<string, byte[]>();
        private readonly ConcurrentDictionary<string, byte> _persistedTrackIndex = new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _trackLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        private readonly DataProtectionProvider _protector = new DataProtectionProvider("LOCAL=user");
        private readonly SemaphoreSlim _initializationGate = new SemaphoreSlim(1, 1);

        private readonly string _volatileKeyFolderPath = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "keys");
        private readonly string _persistedKeyFolderPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "keys");

        private bool _isInitialized;

        public byte[] GetKeySync(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            if (string.IsNullOrWhiteSpace(trackId))
                return null;

            if (_hotCache.TryGetValue(trackId, out var key))
            {
                LogService.Info("[AudioKeyCache.GetKeySync] Key hit.");
                return key;
            }

            LogService.Warn("[AudioKeyCache.GetKeySync] Key miss.");
            return null;
        }

        public bool IsPersisted(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            return !string.IsNullOrWhiteSpace(trackId) && _persistedTrackIndex.ContainsKey(trackId);
        }

        public void MarkPersisted(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            if (!string.IsNullOrWhiteSpace(trackId))
                _persistedTrackIndex[trackId] = 1;
        }

        public void MarkVolatile(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            if (!string.IsNullOrWhiteSpace(trackId))
                _persistedTrackIndex.TryRemove(trackId, out _);
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            await _initializationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_isInitialized)
                    return;

                Directory.CreateDirectory(_volatileKeyFolderPath);
                Directory.CreateDirectory(_persistedKeyFolderPath);

                var persistedCount = await LoadFolderAsync(_persistedKeyFolderPath, persisted: true).ConfigureAwait(false);
                var volatileCount = await LoadFolderAsync(_volatileKeyFolderPath, persisted: false).ConfigureAwait(false);

                DeleteLegacyDatabaseFiles();

                _isInitialized = true;

                System.Diagnostics.Debug.WriteLine("[KeyCache] Volatile + persisted key folders ready.");
                LogService.Info($"[AudioKeyCache.InitializeAsync] Loaded {persistedCount} persisted and {volatileCount} volatile keys.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Init Error: {ex.Message}");
                LogService.Error(ex, "[AudioKeyCache.InitializeAsync] Failed to initialize key cache.");
                throw;
            }
            finally
            {
                _initializationGate.Release();
            }
        }

        public Task AddKeyAsync(string trackId, byte[] rawKey)
        {
            return AddVolatileKeyAsync(trackId, rawKey);
        }

        public async Task AddVolatileKeyAsync(string trackId, byte[] rawKey)
        {
            trackId = NormalizeTrackId(trackId);
            if (string.IsNullOrWhiteSpace(trackId) || rawKey == null || rawKey.Length == 0)
                return;

            await WaitForInitializationAsync().ConfigureAwait(false);

            var trackLock = GetTrackLock(trackId);
            await trackLock.WaitAsync().ConfigureAwait(false);

            try
            {
                var storedKey = rawKey.ToArray();
                await WriteProtectedKeyAsync(GetVolatileKeyPath(trackId), trackId, storedKey).ConfigureAwait(false);
                TryDeleteFile(GetPersistedKeyPath(trackId));

                _hotCache[trackId] = storedKey;
                _persistedTrackIndex.TryRemove(trackId, out _);

                System.Diagnostics.Debug.WriteLine("[KeyCache] Saved volatile key.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Volatile save error: {ex.Message}");
                LogService.Error(ex, "[AudioKeyCache.AddVolatileKeyAsync] Failed to save volatile key.");
            }
            finally
            {
                trackLock.Release();
            }
        }

        public async Task AddPersistedKeyAsync(string trackId, byte[] rawKey)
        {
            trackId = NormalizeTrackId(trackId);
            if (string.IsNullOrWhiteSpace(trackId) || rawKey == null || rawKey.Length == 0)
                return;

            await WaitForInitializationAsync().ConfigureAwait(false);

            var trackLock = GetTrackLock(trackId);
            await trackLock.WaitAsync().ConfigureAwait(false);

            try
            {
                var storedKey = rawKey.ToArray();
                await WriteProtectedKeyAsync(GetPersistedKeyPath(trackId), trackId, storedKey).ConfigureAwait(false);
                TryDeleteFile(GetVolatileKeyPath(trackId));

                _hotCache[trackId] = storedKey;
                _persistedTrackIndex[trackId] = 1;

                System.Diagnostics.Debug.WriteLine("[KeyCache] Saved persisted key.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Persisted save error: {ex.Message}");
                LogService.Error(ex, "[AudioKeyCache.AddPersistedKeyAsync] Failed to save persisted key.");
            }
            finally
            {
                trackLock.Release();
            }
        }

        public async Task RemoveKeyAsync(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            if (string.IsNullOrWhiteSpace(trackId))
                return;

            await WaitForInitializationAsync().ConfigureAwait(false);

            var trackLock = GetTrackLock(trackId);
            await trackLock.WaitAsync().ConfigureAwait(false);

            try
            {
                TryDeleteFile(GetVolatileKeyPath(trackId));
                TryDeleteFile(GetPersistedKeyPath(trackId));
                _hotCache.TryRemove(trackId, out _);
                _persistedTrackIndex.TryRemove(trackId, out _);

                System.Diagnostics.Debug.WriteLine("[KeyCache] Removed key.");
            }
            finally
            {
                trackLock.Release();
            }
        }

        public async Task RemoveVolatileKeyAsync(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            if (string.IsNullOrWhiteSpace(trackId))
                return;

            await WaitForInitializationAsync().ConfigureAwait(false);

            var trackLock = GetTrackLock(trackId);
            await trackLock.WaitAsync().ConfigureAwait(false);

            try
            {
                TryDeleteFile(GetVolatileKeyPath(trackId));

                if (!_persistedTrackIndex.ContainsKey(trackId) && !File.Exists(GetPersistedKeyPath(trackId)))
                    _hotCache.TryRemove(trackId, out _);

                System.Diagnostics.Debug.WriteLine("[KeyCache] Removed volatile key.");
            }
            finally
            {
                trackLock.Release();
            }
        }

        public async Task RemovePersistedKeyAsync(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            if (string.IsNullOrWhiteSpace(trackId))
                return;

            await WaitForInitializationAsync().ConfigureAwait(false);

            var trackLock = GetTrackLock(trackId);
            await trackLock.WaitAsync().ConfigureAwait(false);

            try
            {
                TryDeleteFile(GetPersistedKeyPath(trackId));
                _persistedTrackIndex.TryRemove(trackId, out _);

                if (!File.Exists(GetVolatileKeyPath(trackId)))
                    _hotCache.TryRemove(trackId, out _);

                System.Diagnostics.Debug.WriteLine("[KeyCache] Removed persisted key.");
            }
            finally
            {
                trackLock.Release();
            }
        }

        public async Task MoveKeyToPersistedAsync(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            if (string.IsNullOrWhiteSpace(trackId))
                return;

            await WaitForInitializationAsync().ConfigureAwait(false);

            var trackLock = GetTrackLock(trackId);
            await trackLock.WaitAsync().ConfigureAwait(false);

            try
            {
                MarkPersisted(trackId);

                var volatilePath = GetVolatileKeyPath(trackId);
                var persistedPath = GetPersistedKeyPath(trackId);

                if (File.Exists(volatilePath))
                {
                    CopyFileReplacingDestination(volatilePath, persistedPath);
                    TryDeleteFile(volatilePath);
                    System.Diagnostics.Debug.WriteLine("[KeyCache] Moved key to persisted storage.");
                    return;
                }

                if (_hotCache.TryGetValue(trackId, out var rawKey))
                {
                    await WriteProtectedKeyAsync(persistedPath, trackId, rawKey).ConfigureAwait(false);
                    TryDeleteFile(volatilePath);
                    System.Diagnostics.Debug.WriteLine("[KeyCache] Rebuilt persisted key from hot cache.");
                    return;
                }

                System.Diagnostics.Debug.WriteLine("[KeyCache] MoveKeyToPersistedAsync: no volatile or hot key found.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Move to persisted error: {ex.Message}");
                LogService.Error(ex, "[AudioKeyCache.MoveKeyToPersistedAsync] Failed to move key to persisted storage.");
            }
            finally
            {
                trackLock.Release();
            }
        }

        public async Task MoveKeyToVolatileAsync(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            if (string.IsNullOrWhiteSpace(trackId))
                return;

            await WaitForInitializationAsync().ConfigureAwait(false);

            var trackLock = GetTrackLock(trackId);
            await trackLock.WaitAsync().ConfigureAwait(false);

            try
            {
                MarkVolatile(trackId);

                var persistedPath = GetPersistedKeyPath(trackId);
                var volatilePath = GetVolatileKeyPath(trackId);

                if (File.Exists(persistedPath))
                {
                    CopyFileReplacingDestination(persistedPath, volatilePath);
                    TryDeleteFile(persistedPath);
                    System.Diagnostics.Debug.WriteLine("[KeyCache] Moved key to volatile storage.");
                    return;
                }

                if (_hotCache.TryGetValue(trackId, out var rawKey))
                {
                    await WriteProtectedKeyAsync(volatilePath, trackId, rawKey).ConfigureAwait(false);
                    TryDeleteFile(persistedPath);
                    System.Diagnostics.Debug.WriteLine("[KeyCache] Rebuilt volatile key from hot cache.");
                    return;
                }

                System.Diagnostics.Debug.WriteLine("[KeyCache] MoveKeyToVolatileAsync: no persisted or hot key found.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Move to volatile error: {ex.Message}");
                LogService.Error(ex, "[AudioKeyCache.MoveKeyToVolatileAsync] Failed to move key to volatile storage.");
            }
            finally
            {
                trackLock.Release();
            }
        }

        private async Task<int> LoadFolderAsync(string folderPath, bool persisted)
        {
            var count = 0;
            foreach (var filePath in Directory.EnumerateFiles(folderPath, "*" + KeyExtension))
            {
                if (await LoadKeyFileAsync(filePath, persisted).ConfigureAwait(false))
                    count++;
            }

            return count;
        }

        private async Task<bool> LoadKeyFileAsync(string filePath, bool persisted)
        {
            try
            {
                var protectedData = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
                var payload = await UnprotectPayloadAsync(protectedData).ConfigureAwait(false);
                if (payload == null || string.IsNullOrWhiteSpace(payload.TrackId) || payload.RawKey == null || payload.RawKey.Length == 0)
                {
                    TryDeleteFile(filePath);
                    return false;
                }

                if (!persisted && _persistedTrackIndex.ContainsKey(payload.TrackId))
                {
                    TryDeleteFile(filePath);
                    return false;
                }

                _hotCache[payload.TrackId] = payload.RawKey;

                if (persisted)
                    _persistedTrackIndex[payload.TrackId] = 1;
                else
                    _persistedTrackIndex.TryRemove(payload.TrackId, out _);

                return true;
            }
            catch (Exception ex)
            {
                TryDeleteFile(filePath);
                LogService.Warn($"[AudioKeyCache.LoadKeyFileAsync] Removed unreadable key file: {ex.Message}");
                return false;
            }
        }

        private async Task WriteProtectedKeyAsync(string filePath, string trackId, byte[] rawKey)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            var payload = EncodePayload(trackId, rawKey);
            IBuffer protectedBuffer = await _protector.ProtectAsync(payload.AsBuffer());
            var protectedData = protectedBuffer.ToArray();

            var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(tempPath, protectedData).ConfigureAwait(false);

                if (File.Exists(filePath))
                    File.Delete(filePath);

                File.Move(tempPath, filePath);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }

        private async Task<KeyPayload> UnprotectPayloadAsync(byte[] protectedData)
        {
            IBuffer unprotectedBuffer = await _protector.UnprotectAsync(protectedData.AsBuffer());
            return DecodePayload(unprotectedBuffer.ToArray());
        }

        private string GetVolatileKeyPath(string trackId)
        {
            return Path.Combine(_volatileKeyFolderPath, GetKeyFileName(trackId));
        }

        private string GetPersistedKeyPath(string trackId)
        {
            return Path.Combine(_persistedKeyFolderPath, GetKeyFileName(trackId));
        }

        private static string GetKeyFileName(string trackId)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(trackId));
                return string.Concat(hash.Select(b => b.ToString("x2"))) + KeyExtension;
            }
        }

        private static byte[] EncodePayload(string trackId, byte[] rawKey)
        {
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(trackId);
                writer.Write(rawKey.Length);
                writer.Write(rawKey);
                writer.Flush();
                return memory.ToArray();
            }
        }

        private static KeyPayload DecodePayload(byte[] payload)
        {
            using (var memory = new MemoryStream(payload))
            using (var reader = new BinaryReader(memory, Encoding.UTF8))
            {
                var version = reader.ReadByte();
                if (version != PayloadVersion)
                    return null;

                var trackId = NormalizeTrackId(reader.ReadString());
                var keyLength = reader.ReadInt32();
                if (keyLength <= 0 || keyLength > 4096)
                    return null;

                var rawKey = reader.ReadBytes(keyLength);
                if (rawKey.Length != keyLength)
                    return null;

                return new KeyPayload
                {
                    TrackId = trackId,
                    RawKey = rawKey
                };
            }
        }

        private async Task WaitForInitializationAsync()
        {
            if (!_isInitialized)
                await InitializeAsync().ConfigureAwait(false);
        }

        private SemaphoreSlim GetTrackLock(string trackId)
        {
            return _trackLocks.GetOrAdd(trackId, _ => new SemaphoreSlim(1, 1));
        }

        private void DeleteLegacyDatabaseFiles()
        {
            DeleteLegacyDatabaseFiles(ApplicationData.Current.LocalCacheFolder.Path);
            DeleteLegacyDatabaseFiles(ApplicationData.Current.LocalFolder.Path);
        }

        private static void DeleteLegacyDatabaseFiles(string root)
        {
            foreach (var name in new[] { "keys.db", "keys.db-shm", "keys.db-wal" })
                TryDeleteFile(Path.Combine(root, name));
        }

        private static void CopyFileReplacingDestination(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        private static void TryDeleteFile(string filePath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch
            {
            }
        }

        private static string NormalizeTrackId(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return trackId;

            return trackId.Trim().ToLowerInvariant().PadLeft(32, '0');
        }

        private sealed class KeyPayload
        {
            public string TrackId { get; set; }
            public byte[] RawKey { get; set; }
        }
    }
}
