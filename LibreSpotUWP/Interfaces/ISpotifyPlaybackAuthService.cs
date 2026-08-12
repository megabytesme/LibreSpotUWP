using LibreSpotUWP.Models;
using System;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface ISpotifyPlaybackAuthService
    {
        PlaybackAuthState Current { get; }
        event EventHandler<PlaybackAuthState> PlaybackAuthStateChanged;

        Task InitializeAsync();
        Task<Uri> BeginBrowserAuthorizationAsync();
        Task CompleteBrowserAuthorizationAsync(string callbackUri, string accountId);
        Task ValidateImportAsync(PlaybackAuthorizationPackage package);
        Task<PlaybackConnectionMaterial> GetConnectionMaterialAsync();
        Task ImportAsync(PlaybackAuthorizationPackage package, string accountId);
        Task SaveReusableCredentialsAsync(string credentialsJson, string sessionUser);
        Task MarkRejectedAsync();
        Task ResetAsync();
        Task<PlaybackAuthorizationPackage> ExportAsync();
    }
}
