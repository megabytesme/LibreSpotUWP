using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public sealed class FileMetadataCache : IMetadataCache
{
    private readonly IFileSystem _fileSystem;
    private readonly string _root;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new ConcurrentDictionary<string, SemaphoreSlim>();

    private sealed class CacheEnvelope<T>
    {
        public DateTimeOffset Timestamp { get; set; }
        public T Data { get; set; }
    }

    public FileMetadataCache(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        _root = _fileSystem.Combine(_fileSystem.AppDataDirectory, "cache");
    }

    private SemaphoreSlim GetLock(string path)
        => _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    public async Task<CacheResponse<T>> GetOrAddAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan ttl,
        bool forceRefresh = false)
    {
        var path = GetPathForKey(key);
        var fileLock = GetLock(path);

        await fileLock.WaitAsync();
        try
        {
            if (!forceRefresh && await _fileSystem.FileExistsAsync(path))
            {
                try
                {
                    var json = await _fileSystem.ReadTextAsync(path);
                    var envelope = JsonConvert.DeserializeObject<CacheEnvelope<T>>(json);

                    if (envelope != null)
                    {
                        if (ttl == TimeSpan.Zero || ttl == TimeSpan.MaxValue)
                            return new CacheResponse<T>(envelope.Data, envelope.Timestamp, true);

                        if (DateTimeOffset.UtcNow - envelope.Timestamp < ttl)
                            return new CacheResponse<T>(envelope.Data, envelope.Timestamp, true);
                    }
                }
                catch
                {
                }
            }

            var fresh = await factory();

            var newEnvelope = new CacheEnvelope<T>
            {
                Timestamp = DateTimeOffset.UtcNow,
                Data = fresh
            };

            var jsonOut = JsonConvert.SerializeObject(newEnvelope);

            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                await _fileSystem.CreateFolderAsync(folder);

            await _fileSystem.WriteTextAsync(path, jsonOut);

            return new CacheResponse<T>(fresh, newEnvelope.Timestamp, false);
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task InvalidateAsync(string key)
    {
        var path = GetPathForKey(key);
        var fileLock = GetLock(path);

        await fileLock.WaitAsync();
        try
        {
            if (await _fileSystem.FileExistsAsync(path))
                await _fileSystem.DeleteFileAsync(path);
        }
        finally
        {
            fileLock.Release();
        }
    }

    private string GetPathForKey(string key)
    {
        var relative = key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? key
            : key + ".json";

        return _fileSystem.Combine(_root, relative);
    }
}