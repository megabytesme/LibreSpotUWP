using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LibreSpotUWP.Interfaces;
using Newtonsoft.Json.Linq;

namespace LibreSpotUWP.Services
{
    public sealed class FileMetadataCache : IMetadataCache
    {
        private readonly IFileSystem _fs;
        private readonly string _folder;

        public FileMetadataCache(IFileSystem fs)
        {
            _fs = fs;
            _folder = _fs.Combine(_fs.AppDataDirectory, "Cache");
        }

        public async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl)
        {
            await _fs.CreateFolderAsync(_folder);

            var path = _fs.Combine(_folder, HashKey(key));

            if (await _fs.FileExistsAsync(path))
            {
                try
                {
                    var text = await _fs.ReadTextAsync(path);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var json = JObject.Parse(text);

                        long expires = json["expires"] != null ? json["expires"].ToObject<long>() : 0;
                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                        if (now < expires)
                        {
                            return json["payload"].ToObject<T>();
                        }
                    }
                }
                catch
                {
                }

                await _fs.DeleteFileAsync(path);
            }

            var value = await factory();

            var envelope = new JObject
            {
                ["expires"] = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeMilliseconds(),
                ["payload"] = JToken.FromObject(value)
            };

            await _fs.WriteTextAsync(path, envelope.ToString());

            return value;
        }

        public Task InvalidateAsync(string key)
        {
            var path = _fs.Combine(_folder, HashKey(key));
            return _fs.DeleteFileAsync(path);
        }

        private static string HashKey(string key)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return BitConverter.ToString(bytes, 0, 16)
                    .Replace("-", "")
                    .ToLowerInvariant() + ".json";
            }
        }
    }
}