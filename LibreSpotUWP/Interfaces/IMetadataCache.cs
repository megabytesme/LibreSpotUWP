using LibreSpotUWP.Models;
using System;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface IMetadataCache
    {
        Task<CacheResponse<T>> GetOrAddAsync<T>(
            string key,
            Func<Task<T>> factory,
            TimeSpan ttl,
            bool forceRefresh = false);

        Task<CacheResponse<T>> TryGetAsync<T>(string key);

        Task InvalidateAsync(string key);
    }
}
