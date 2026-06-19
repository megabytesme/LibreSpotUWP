using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private const string KeyFolderName = "keys";
        private const byte PayloadVersion = 1;

        private readonly ConcurrentDictionary<string, byte[]> _hotCache = new ConcurrentDictionary<string, byte[]>();
        private readonly ConcurrentDictionary<string, byte> _persistedTrackIndex = new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _trackLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        private readonly DataProtectionProvider _protector = new DataProtectionProvider("LOCAL=user");
        private readonly SemaphoreSlim _initializationGate = new SemaphoreSlim(1, 1);

        private StorageFolder _volatileKeyFolder;
        private StorageFolder _persistedKeyFolder;
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

                var volatileFolder = await GetVolatileKeyFolderAsync().ConfigureAwait(false);
                var persistedFolder = await GetPersistedKeyFolderAsync().ConfigureAwait(false);

                var persistedCount = await LoadFolderAsync(persistedFolder, persisted: true).ConfigureAwait(false);
                var volatileCount = await LoadFolderAsync(volatileFolder, persisted: false).ConfigureAwait(false);

                await DeleteLegacyDatabaseFilesAsync().ConfigureAwait(false);

                _isInitialized = true;

                System.Diagnostics.Debug.WriteLine("[KeyCache] Volatile + persisted key folders ready.");
                LogService.Info($"[AudioKeyCache.InitializeAsync] Loaded {persistedCount} persisted and {volatileCount} volatile keys.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Init Error: {ex.Message}");
                LogService.Error(ex, "[AudioKeyCache.InitializeAsync] Failed to initialize key cache. Continuing without preloaded keys.");
                _isInitialized = true;
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
                var fileName = GetKeyFileName(trackId);
                await WriteProtectedKeyAsync(await GetVolatileKeyFolderAsync().ConfigureAwait(false), fileName, trackId, storedKey).ConfigureAwait(false);
                await TryDeleteStorageFileAsync(await GetPersistedKeyFolderAsync().ConfigureAwait(false), fileName).ConfigureAwait(false);

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
                var fileName = GetKeyFileName(trackId);
                await WriteProtectedKeyAsync(await GetPersistedKeyFolderAsync().ConfigureAwait(false), fileName, trackId, storedKey).ConfigureAwait(false);
                await TryDeleteStorageFileAsync(await GetVolatileKeyFolderAsync().ConfigureAwait(false), fileName).ConfigureAwait(false);

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
                var fileName = GetKeyFileName(trackId);
                await TryDeleteStorageFileAsync(await GetVolatileKeyFolderAsync().ConfigureAwait(false), fileName).ConfigureAwait(false);
                await TryDeleteStorageFileAsync(await GetPersistedKeyFolderAsync().ConfigureAwait(false), fileName).ConfigureAwait(false);
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
                var fileName = GetKeyFileName(trackId);
                await TryDeleteStorageFileAsync(await GetVolatileKeyFolderAsync().ConfigureAwait(false), fileName).ConfigureAwait(false);

                if (!_persistedTrackIndex.ContainsKey(trackId) && !await StorageFileExistsAsync(await GetPersistedKeyFolderAsync().ConfigureAwait(false), fileName).ConfigureAwait(false))
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
                var fileName = GetKeyFileName(trackId);
                await TryDeleteStorageFileAsync(await GetPersistedKeyFolderAsync().ConfigureAwait(false), fileName).ConfigureAwait(false);
                _persistedTrackIndex.TryRemove(trackId, out _);

                if (!await StorageFileExistsAsync(await GetVolatileKeyFolderAsync().ConfigureAwait(false), fileName).ConfigureAwait(false))
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

                var fileName = GetKeyFileName(trackId);
                var volatileFolder = await GetVolatileKeyFolderAsync().ConfigureAwait(false);
                var persistedFolder = await GetPersistedKeyFolderAsync().ConfigureAwait(false);
                var volatileFile = await TryGetStorageFileAsync(volatileFolder, fileName).ConfigureAwait(false);

                if (volatileFile != null && await CopyFileReplacingDestinationAsync(volatileFile, persistedFolder).ConfigureAwait(false))
                {
                    await TryDeleteStorageFileAsync(volatileFile).ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine("[KeyCache] Moved key to persisted storage.");
                    return;
                }

                if (_hotCache.TryGetValue(trackId, out var rawKey))
                {
                    await WriteProtectedKeyAsync(persistedFolder, fileName, trackId, rawKey).ConfigureAwait(false);
                    await TryDeleteStorageFileAsync(volatileFolder, fileName).ConfigureAwait(false);
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

                var fileName = GetKeyFileName(trackId);
                var persistedFolder = await GetPersistedKeyFolderAsync().ConfigureAwait(false);
                var volatileFolder = await GetVolatileKeyFolderAsync().ConfigureAwait(false);
                var persistedFile = await TryGetStorageFileAsync(persistedFolder, fileName).ConfigureAwait(false);

                if (persistedFile != null && await CopyFileReplacingDestinationAsync(persistedFile, volatileFolder).ConfigureAwait(false))
                {
                    await TryDeleteStorageFileAsync(persistedFile).ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine("[KeyCache] Moved key to volatile storage.");
                    return;
                }

                if (_hotCache.TryGetValue(trackId, out var rawKey))
                {
                    await WriteProtectedKeyAsync(volatileFolder, fileName, trackId, rawKey).ConfigureAwait(false);
                    await TryDeleteStorageFileAsync(persistedFolder, fileName).ConfigureAwait(false);
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

        private async Task<int> LoadFolderAsync(StorageFolder folder, bool persisted)
        {
            if (folder == null)
                return 0;

            IReadOnlyList<StorageFile> files;
            try
            {
                files = await folder.GetFilesAsync();
            }
            catch (Exception ex)
            {
                LogService.Warn($"[AudioKeyCache.LoadFolderAsync] Failed to enumerate key folder: {ex.Message}");
                return 0;
            }

            var count = 0;
            foreach (var file in files)
            {
                if (!file.Name.EndsWith(KeyExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (await LoadKeyFileAsync(file, persisted).ConfigureAwait(false))
                    count++;
            }

            return count;
        }

        private async Task<bool> LoadKeyFileAsync(StorageFile file, bool persisted)
        {
            try
            {
                var protectedData = await FileIO.ReadBufferAsync(file);
                var payload = await UnprotectPayloadAsync(protectedData).ConfigureAwait(false);
                if (payload == null || string.IsNullOrWhiteSpace(payload.TrackId) || payload.RawKey == null || payload.RawKey.Length == 0)
                {
                    await TryDeleteStorageFileAsync(file).ConfigureAwait(false);
                    return false;
                }

                if (!persisted && _persistedTrackIndex.ContainsKey(payload.TrackId))
                {
                    await TryDeleteStorageFileAsync(file).ConfigureAwait(false);
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
                await TryDeleteStorageFileAsync(file).ConfigureAwait(false);
                LogService.Warn($"[AudioKeyCache.LoadKeyFileAsync] Removed unreadable key file: {ex.Message}");
                return false;
            }
        }

        private async Task WriteProtectedKeyAsync(StorageFolder folder, string fileName, string trackId, byte[] rawKey)
        {
            if (folder == null)
            {
                LogService.Warn("[AudioKeyCache.WriteProtectedKeyAsync] Key folder is unavailable; skipping key write.");
                return;
            }

            var payload = EncodePayload(trackId, rawKey);
            IBuffer protectedBuffer = await _protector.ProtectAsync(payload.AsBuffer());

            StorageFile tempFile = null;
            var committed = false;
            try
            {
                var tempName = fileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
                tempFile = await folder.CreateFileAsync(tempName, CreationCollisionOption.FailIfExists);
                await FileIO.WriteBufferAsync(tempFile, protectedBuffer);
                await tempFile.RenameAsync(fileName, NameCollisionOption.ReplaceExisting);
                committed = true;
            }
            finally
            {
                if (!committed)
                    await TryDeleteStorageFileAsync(tempFile).ConfigureAwait(false);
            }
        }

        private async Task<KeyPayload> UnprotectPayloadAsync(IBuffer protectedData)
        {
            IBuffer unprotectedBuffer = await _protector.UnprotectAsync(protectedData);
            return DecodePayload(unprotectedBuffer.ToArray());
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

        private async Task<StorageFolder> GetVolatileKeyFolderAsync()
        {
            if (_volatileKeyFolder != null)
                return _volatileKeyFolder;

            _volatileKeyFolder = await GetOrCreateKeyFolderAsync(ApplicationData.Current.LocalCacheFolder, "LocalCacheFolder").ConfigureAwait(false);
            return _volatileKeyFolder;
        }

        private async Task<StorageFolder> GetPersistedKeyFolderAsync()
        {
            if (_persistedKeyFolder != null)
                return _persistedKeyFolder;

            _persistedKeyFolder = await GetOrCreateKeyFolderAsync(ApplicationData.Current.LocalFolder, "LocalFolder").ConfigureAwait(false);
            return _persistedKeyFolder;
        }

        private static async Task<StorageFolder> GetOrCreateKeyFolderAsync(StorageFolder rootFolder, string label)
        {
            try
            {
                return await rootFolder.CreateFolderAsync(KeyFolderName, CreationCollisionOption.OpenIfExists);
            }
            catch (Exception ex)
            {
                LogService.Warn($"[AudioKeyCache.GetOrCreateKeyFolderAsync] Unable to open {label}\\{KeyFolderName}: {ex.Message}");
                return null;
            }
        }

        private static async Task<bool> StorageFileExistsAsync(StorageFolder folder, string fileName)
        {
            var file = await TryGetStorageFileAsync(folder, fileName).ConfigureAwait(false);
            return file != null;
        }

        private static async Task<StorageFile> TryGetStorageFileAsync(StorageFolder folder, string fileName)
        {
            if (folder == null || string.IsNullOrWhiteSpace(fileName))
                return null;

            try
            {
                return await folder.GetFileAsync(fileName);
            }
            catch
            {
                return null;
            }
        }

        private static async Task TryDeleteStorageFileAsync(StorageFolder folder, string fileName)
        {
            await TryDeleteStorageFileAsync(await TryGetStorageFileAsync(folder, fileName).ConfigureAwait(false)).ConfigureAwait(false);
        }

        private static async Task TryDeleteStorageFileAsync(StorageFile file)
        {
            try
            {
                if (file != null)
                    await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
            }
        }

        private static async Task<bool> CopyFileReplacingDestinationAsync(StorageFile sourceFile, StorageFolder destinationFolder)
        {
            if (sourceFile == null || destinationFolder == null)
                return false;

            try
            {
                await sourceFile.CopyAsync(destinationFolder, sourceFile.Name, NameCollisionOption.ReplaceExisting);
                return true;
            }
            catch (Exception ex)
            {
                LogService.Warn($"[AudioKeyCache.CopyFileReplacingDestinationAsync] Failed to copy key file: {ex.Message}");
                return false;
            }
        }

        private async Task DeleteLegacyDatabaseFilesAsync()
        {
            await DeleteLegacyDatabaseFilesAsync(ApplicationData.Current.LocalCacheFolder).ConfigureAwait(false);
            await DeleteLegacyDatabaseFilesAsync(ApplicationData.Current.LocalFolder).ConfigureAwait(false);
        }

        private static async Task DeleteLegacyDatabaseFilesAsync(StorageFolder rootFolder)
        {
            foreach (var name in new[] { "keys.db", "keys.db-shm", "keys.db-wal" })
                await TryDeleteStorageFileAsync(rootFolder, name).ConfigureAwait(false);
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
