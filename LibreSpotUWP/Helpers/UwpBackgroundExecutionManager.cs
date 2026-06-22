using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Services;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.ExtendedExecution;

namespace LibreSpotUWP.Helpers
{
    public class UwpBackgroundExecutionManager : IBackgroundExecutionManager
    {
        private ExtendedExecutionSession _session;
        private bool _revoked;

        public async Task<bool> RequestKeepAliveAsync()
        {
            if (_session != null && !_revoked)
                return true;

            if (_revoked)
            {
                _session?.Dispose();
                _session = null;
                _revoked = false;
            }

            var accessStatus = await BackgroundExecutionManager.RequestAccessAsync();

#if UWP1709
            if (accessStatus == BackgroundAccessStatus.AlwaysAllowed ||
                accessStatus == BackgroundAccessStatus.AllowedSubjectToSystemPolicy ||
                accessStatus == BackgroundAccessStatus.AllowedMayUseActiveRealTimeConnectivity ||
                accessStatus == BackgroundAccessStatus.AllowedWithAlwaysOnRealTimeConnectivity)
            {
                return await StartExtendedSessionAsync();
            }
#else
            if (accessStatus == BackgroundAccessStatus.AllowedMayUseActiveRealTimeConnectivity ||
                accessStatus == BackgroundAccessStatus.AllowedWithAlwaysOnRealTimeConnectivity)
            {
                return await StartExtendedSessionAsync();
            }
#endif

            LogService.Warn($"Background access denied: {accessStatus}");
            return false;
        }

        private async Task<bool> StartExtendedSessionAsync()
        {
            if (_session != null)
                return true;

            try
            {
                _session = new ExtendedExecutionSession
                {
                    Reason = ExtendedExecutionReason.Unspecified,
                    Description = "LibreSpotUWP is connected to Spotify."
                };

                _session.Revoked += Session_Revoked;

                var result = await _session.RequestExtensionAsync();

                if (result == ExtendedExecutionResult.Allowed)
                {
                    LogService.Info("Extended execution allowed.");
                    return true;
                }

                LogService.Warn("Extended execution denied.");
                _session.Dispose();
                _session = null;
                return false;
            }
            catch (Exception ex)
            {
                LogService.Warn($"Extended execution request failed: {ex.Message}");
                return false;
            }
        }

        public void StopKeepAlive()
        {
            if (_session != null)
            {
                _session.Revoked -= Session_Revoked;
                _session.Dispose();
                _session = null;
                LogService.Info("Extended execution stopped.");
            }
        }

        private void Session_Revoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            LogService.Warn($"Extended execution session revoked. reason={args.Reason}");
            _revoked = true;
        }
    }
}
