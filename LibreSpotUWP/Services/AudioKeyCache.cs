using SQLite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private readonly DataProtectionProvider _protector = new DataProtectionProvider("LOCAL=user");
        private readonly string _dbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "keys.db");

        private readonly SQLiteAsyncConnection _db;
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(0, 1);
        private bool _isInitialized = false;

        public AudioKeyCache()
        {
            _db = new SQLiteAsyncConnection(_dbPath);
        }

        public byte[] GetKeySync(string trackId)
        {
            return _hotCache.TryGetValue(trackId, out var key) ? key : null;
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            try
            {
                await _db.CreateTableAsync<CachedKey>();
                var allKeys = await _db.Table<CachedKey>().ToListAsync();

                if (allKeys.Any())
                {
                    var decryptionTasks = allKeys.Select(k => DecryptAndCacheAsync(k.TrackId, k.ProtectedKey));
                    await Task.WhenAll(decryptionTasks);
                }

                _isInitialized = true;
                _initLock.Release();
                System.Diagnostics.Debug.WriteLine("[KeyCache] Database and HotCache Ready.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Init Error: {ex.Message}");
            }
        }

        private async Task DecryptAndCacheAsync(string trackId, byte[] protectedData)
        {
            try
            {
                IBuffer unprotectedBuffer = await _protector.UnprotectAsync(protectedData.AsBuffer());
                _hotCache.TryAdd(trackId, unprotectedBuffer.ToArray());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Decryption failed for {trackId}: {ex.Message}");
            }
        }

        public async Task AddKeyAsync(string trackId, byte[] rawKey)
        {
            if (!_hotCache.TryAdd(trackId, rawKey)) return;

            if (!_isInitialized)
            {
                await _initLock.WaitAsync();
                _initLock.Release();
            }

            try
            {
                IBuffer protectedBuffer = await _protector.ProtectAsync(rawKey.AsBuffer());
                var entry = new CachedKey { TrackId = trackId, ProtectedKey = protectedBuffer.ToArray() };
                await _db.InsertOrReplaceAsync(entry);
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Persisted key for {trackId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] DB Save Error: {ex.Message}");
            }
        }

        public async Task RemoveKeyAsync(string trackId)
        {
            _hotCache.TryRemove(trackId, out _);

            try
            {
                await _db.DeleteAsync<CachedKey>(trackId);
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Evicted key for {trackId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] DB Delete Error: {ex.Message}");
            }
        }
    }
}