using LibreSpotUWP.Interfaces;
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

            System.Diagnostics.Debug.WriteLine($"[Background] Access Denied: {accessStatus}");
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
                    System.Diagnostics.Debug.WriteLine("[Background] Extended Execution Allowed.");
                    return true;
                }

                System.Diagnostics.Debug.WriteLine("[Background] Extended Execution Denied.");
                _session.Dispose();
                _session = null;
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Background] Error: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine("[Background] Extended Execution Stopped.");
            }
        }

        private void Session_Revoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine($"[Background] Session Revoked! Reason: {args.Reason}");
            _revoked = true;
        }
    }
}