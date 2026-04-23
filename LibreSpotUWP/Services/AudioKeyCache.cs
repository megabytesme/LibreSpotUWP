using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LibreSpotUWP.Services
{
    public sealed class AudioKeyCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _hotCache =
            new ConcurrentDictionary<string, byte[]>();
        private readonly DataProtectionProvider _protector = new DataProtectionProvider(
            "LOCAL=user"
        );
        private readonly string _dbPath = Path.Combine(
            ApplicationData.Current.LocalCacheFolder.Path,
            "keys.db"
        );

        public byte[] GetKeySync(string trackId)
        {
            return _hotCache.TryGetValue(trackId, out var key) ? key : null;
        }

        public async Task InitializeAsync()
        {
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                await connection.OpenAsync();

                var createTable = connection.CreateCommand();
                createTable.CommandText =
                    "CREATE TABLE IF NOT EXISTS AudioKeys (TrackId TEXT PRIMARY KEY, ProtectedKey BLOB)";
                await createTable.ExecuteNonQueryAsync();

                var selectCmd = connection.CreateCommand();
                selectCmd.CommandText = "SELECT TrackId, ProtectedKey FROM AudioKeys";

                using (var reader = await selectCmd.ExecuteReaderAsync())
                {
                    var decryptionTasks = new List<Task>();

                    while (await reader.ReadAsync())
                    {
                        string trackId = reader.GetString(0);
                        byte[] protectedKey = (byte[])reader.GetValue(1);

                        decryptionTasks.Add(DecryptAndCacheAsync(trackId, protectedKey));
                    }

                    await Task.WhenAll(decryptionTasks);
                }
            }
        }

        private async Task DecryptAndCacheAsync(string trackId, byte[] protectedData)
        {
            try
            {
                IBuffer unprotectedBuffer = await _protector.UnprotectAsync(
                    protectedData.AsBuffer()
                );
                _hotCache.TryAdd(trackId, unprotectedBuffer.ToArray());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[KeyCache] Failed to decrypt key for {trackId}: {ex.Message}"
                );
            }
        }

        public async Task AddKeyAsync(string trackId, byte[] rawKey)
        {
            if (string.IsNullOrEmpty(trackId) || rawKey == null || rawKey.Length != 16)
                return;
            if (_hotCache.ContainsKey(trackId))
                return;

            _hotCache.TryAdd(trackId, rawKey);

            try
            {
                IBuffer protectedBuffer = await _protector.ProtectAsync(rawKey.AsBuffer());
                byte[] protectedBytes = protectedBuffer.ToArray();

                using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
                {
                    await connection.OpenAsync();
                    var upsertCmd = connection.CreateCommand();
                    upsertCmd.CommandText =
                        "INSERT OR REPLACE INTO AudioKeys (TrackId, ProtectedKey) VALUES (@id, @key)";
                    upsertCmd.Parameters.AddWithValue("@id", trackId);
                    upsertCmd.Parameters.AddWithValue("@key", protectedBytes);
                    await upsertCmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyCache] Error saving key: {ex.Message}");
            }
        }
    }
}
