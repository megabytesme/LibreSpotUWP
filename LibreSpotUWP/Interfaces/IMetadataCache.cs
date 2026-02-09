using System;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface IMetadataCache
    {
        Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl);
        Task InvalidateAsync(string key);
    }
}