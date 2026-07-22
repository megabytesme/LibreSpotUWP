using System;

namespace LibreSpotUWP.Services
{
    internal enum SpotifyConnectTransferObservation
    {
        NoPendingTransfer,
        IgnoreStaleSnapshot,
        Confirmed,
        Expired
    }

    /// <summary>
    /// Protects a user-requested Connect transfer from being overwritten by
    /// the Web API's previous active-device snapshot while the transfer is in
    /// flight.
    /// </summary>
    internal sealed class SpotifyConnectTransferTracker
    {
        private readonly object _lock = new object();
        private readonly TimeSpan _confirmationTimeout;
        private string _targetDeviceId;
        private DateTimeOffset _expiresAt;

        public SpotifyConnectTransferTracker(TimeSpan confirmationTimeout)
        {
            if (confirmationTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(confirmationTimeout));

            _confirmationTimeout = confirmationTimeout;
        }

        public void Begin(string targetDeviceId, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(targetDeviceId))
                throw new ArgumentException("A Connect target device is required.", nameof(targetDeviceId));

            lock (_lock)
            {
                _targetDeviceId = targetDeviceId;
                _expiresAt = now.Add(_confirmationTimeout);
            }
        }

        public void Cancel()
        {
            lock (_lock)
            {
                _targetDeviceId = null;
                _expiresAt = DateTimeOffset.MinValue;
            }
        }

        public SpotifyConnectTransferObservation Observe(string activeDeviceId, DateTimeOffset now)
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(_targetDeviceId))
                    return SpotifyConnectTransferObservation.NoPendingTransfer;

                if (string.Equals(_targetDeviceId, activeDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    _targetDeviceId = null;
                    _expiresAt = DateTimeOffset.MinValue;
                    return SpotifyConnectTransferObservation.Confirmed;
                }

                if (now >= _expiresAt)
                {
                    _targetDeviceId = null;
                    _expiresAt = DateTimeOffset.MinValue;
                    return SpotifyConnectTransferObservation.Expired;
                }

                return SpotifyConnectTransferObservation.IgnoreStaleSnapshot;
            }
        }
    }
}
