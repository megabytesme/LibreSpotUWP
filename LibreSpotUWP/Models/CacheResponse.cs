using System;

namespace LibreSpotUWP.Models
{
    public sealed class CacheResponse<T>
    {
        public T Value { get; }
        public DateTimeOffset Timestamp { get; }
        public bool IsFromCache { get; }
        public bool IsStale { get; }
        public bool IsOfflineFallback { get; }

        public TimeSpan Age => DateTimeOffset.UtcNow - Timestamp;

        public CacheResponse(
            T value,
            DateTimeOffset timestamp,
            bool isFromCache,
            bool isStale = false,
            bool isOfflineFallback = false)
        {
            Value = value;
            Timestamp = timestamp;
            IsFromCache = isFromCache;
            IsStale = isStale;
            IsOfflineFallback = isOfflineFallback;
        }
    }
}
