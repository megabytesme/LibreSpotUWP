using SQLite;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LibreSpotUWP.Services
{
    public class CachedKey
    {
        [PrimaryKey]
        public string TrackId { get; set; }
        public byte[] ProtectedKey { get; set; }
    }

    public sealed class AudioKeyCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _hotCache = new ConcurrentDictionary<string, byte[]>();
        private readonly ConcurrentDictionary<string, byte> _persistedTrackIndex = new ConcurrentDictionary<string, byte>();

        private readonly DataProtectionProvider _protector = new DataProtectionProvider("LOCAL=user");

        private readonly string _volatileDbPath = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "keys.db");
        private readonly string _persistedDbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "keys.db");

        private readonly SQLiteAsyncConnection _volatileDb;
        private readonly SQLiteAsyncConnection _persistedDb;

        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(0, 1);
        private bool _isInitialized = false;

        public AudioKeyCache()
        {
            _volatileDb = new SQLiteAsyncConnection(_volatileDbPath);
            _persistedDb = new SQLiteAsyncConnection(_persistedDbPath);
        }

        public byte[] GetKeySync(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            if (_hotCache.TryGetValue(trackId, out var key))
            {
                LogService.Info($"[AudioKeyCache.GetKeySync] Key hit for trackId={trackId}.");
                return key;
            }

            LogService.Warn($"[AudioKeyCache.GetKeySync] Key miss for trackId={trackId}.");
            return null;
        }

        public bool IsPersisted(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            return _persistedTrackIndex.ContainsKey(trackId);
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
            if (_isInitialized) return;

            try
            {
                await _volatileDb.CreateTableAsync<CachedKey>();
                await _persistedDb.CreateTableAsync<CachedKey>();

                var persistedKeys = await _persistedDb.Table<CachedKey>().ToListAsync();
                var volatileKeys = await _volatileDb.Table<CachedKey>().ToListAsync();

                if (persistedKeys.Any())
                {
                    var persistedTasks = persistedKeys.Select(k => DecryptAndCacheAsync(k.TrackId, k.ProtectedKey, true));
                    await Task.WhenAll(persistedTasks);
                }

                if (volatileKeys.Any())
                {
                    var volatileTasks = volatileKeys.Select(k => DecryptAndCacheAsync(k.TrackId, k.ProtectedKey, false));
                    await Task.WhenAll(volatileTasks);
                }

                _isInitialized = true;
                _initLock.Release();

                System.Diagnostics.Debug.WriteLine("[KeyCache] Volatile + Persisted databases ready.");
                LogService.Info($"[AudioKeyCache.InitializeAsync] Loaded {persistedKeys.Count} persisted and {volatileKeys.Count} volatile keys.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Init Error: {ex.Message}");
                LogService.Error(ex, "[AudioKeyCache.InitializeAsync] Failed to initialize key cache.");
            }
        }

        private async Task DecryptAndCacheAsync(string trackId, byte[] protectedData, bool persisted)
        {
            trackId = NormalizeTrackId(trackId);
            try
            {
                IBuffer unprotectedBuffer = await _protector.UnprotectAsync(protectedData.AsBuffer());
                _hotCache[trackId] = unprotectedBuffer.ToArray();

                if (persisted)
                    _persistedTrackIndex[trackId] = 1;
                else
                    _persistedTrackIndex.TryRemove(trackId, out _);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Decryption failed for {trackId}: {ex.Message}");
                LogService.Error(ex, $"[AudioKeyCache.DecryptAndCacheAsync] Failed to decrypt key for trackId={trackId}.");
            }
        }

        public Task AddKeyAsync(string trackId, byte[] rawKey)
        {
            return AddVolatileKeyAsync(trackId, rawKey);
        }

        public async Task AddVolatileKeyAsync(string trackId, byte[] rawKey)
        {
            trackId = NormalizeTrackId(trackId);
            if (!_isInitialized)
            {
                await _initLock.WaitAsync();
                _initLock.Release();
            }

            try
            {
                _hotCache[trackId] = rawKey;
                _persistedTrackIndex.TryRemove(trackId, out _);

                IBuffer protectedBuffer = await _protector.ProtectAsync(rawKey.AsBuffer());
                var entry = new CachedKey
                {
                    TrackId = trackId,
                    ProtectedKey = protectedBuffer.ToArray()
                };

                await _volatileDb.InsertOrReplaceAsync(entry);
                await _persistedDb.DeleteAsync<CachedKey>(trackId);

                System.Diagnostics.Debug.WriteLine($"[KeyCache] Persisted volatile key for {trackId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Volatile DB Save Error: {ex.Message}");
            }
        }

        public async Task AddPersistedKeyAsync(string trackId, byte[] rawKey)
        {
            trackId = NormalizeTrackId(trackId);
            if (!_isInitialized)
            {
                await _initLock.WaitAsync();
                _initLock.Release();
            }

            try
            {
                _hotCache[trackId] = rawKey;
                _persistedTrackIndex[trackId] = 1;

                IBuffer protectedBuffer = await _protector.ProtectAsync(rawKey.AsBuffer());
                var entry = new CachedKey
                {
                    TrackId = trackId,
                    ProtectedKey = protectedBuffer.ToArray()
                };

                await _persistedDb.InsertOrReplaceAsync(entry);
                await _volatileDb.DeleteAsync<CachedKey>(trackId);

                System.Diagnostics.Debug.WriteLine($"[KeyCache] Persisted durable key for {trackId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Persisted DB Save Error: {ex.Message}");
            }
        }

        public async Task RemoveKeyAsync(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            _hotCache.TryRemove(trackId, out _);
            _persistedTrackIndex.TryRemove(trackId, out _);

            try
            {
                await _volatileDb.DeleteAsync<CachedKey>(trackId);
                await _persistedDb.DeleteAsync<CachedKey>(trackId);
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Removed key for {trackId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] DB Delete Error: {ex.Message}");
            }
        }

        public async Task RemoveVolatileKeyAsync(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            try
            {
                await _volatileDb.DeleteAsync<CachedKey>(trackId);

                if (!_persistedTrackIndex.ContainsKey(trackId))
                {
                    _hotCache.TryRemove(trackId, out _);
                }

                System.Diagnostics.Debug.WriteLine($"[KeyCache] Removed volatile key for {trackId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Volatile DB Delete Error: {ex.Message}");
            }
        }

        public async Task RemovePersistedKeyAsync(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            try
            {
                await _persistedDb.DeleteAsync<CachedKey>(trackId);
                _persistedTrackIndex.TryRemove(trackId, out _);

                bool stillVolatile = await _volatileDb.Table<CachedKey>()
                    .Where(x => x.TrackId == trackId)
                    .CountAsync() > 0;

                if (!stillVolatile)
                {
                    _hotCache.TryRemove(trackId, out _);
                }

                System.Diagnostics.Debug.WriteLine($"[KeyCache] Removed persisted key for {trackId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Persisted DB Delete Error: {ex.Message}");
            }
        }

        public async Task MoveKeyToPersistedAsync(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            if (!_isInitialized)
            {
                await _initLock.WaitAsync();
                _initLock.Release();
            }

            try
            {
                MarkPersisted(trackId);

                var existing = await _volatileDb.FindAsync<CachedKey>(trackId);
                if (existing == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[KeyCache] MoveKeyToPersistedAsync: no volatile key found for {trackId}");
                    return;
                }

                await _persistedDb.InsertOrReplaceAsync(existing);
                await _volatileDb.DeleteAsync<CachedKey>(trackId);

                System.Diagnostics.Debug.WriteLine($"[KeyCache] Moved key to persisted DB for {trackId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] MoveKeyToPersistedAsync Error: {ex.Message}");
            }
        }

        public async Task MoveKeyToVolatileAsync(string trackId)
        {
            trackId = NormalizeTrackId(trackId);
            if (!_isInitialized)
            {
                await _initLock.WaitAsync();
                _initLock.Release();
            }

            try
            {
                MarkVolatile(trackId);

                var existing = await _persistedDb.FindAsync<CachedKey>(trackId);
                if (existing == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[KeyCache] MoveKeyToVolatileAsync: no persisted key found for {trackId}");
                    return;
                }

                await _volatileDb.InsertOrReplaceAsync(existing);
                await _persistedDb.DeleteAsync<CachedKey>(trackId);

                System.Diagnostics.Debug.WriteLine($"[KeyCache] Moved key to volatile DB for {trackId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] MoveKeyToVolatileAsync Error: {ex.Message}");
            }
        }

        private static string NormalizeTrackId(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return trackId;

            return trackId.Trim().ToLowerInvariant().PadLeft(32, '0');
        }
    }
}
