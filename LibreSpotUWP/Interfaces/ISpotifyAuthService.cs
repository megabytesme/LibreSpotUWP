using LibreSpotUWP.Models;
using System;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface ISpotifyAuthService
    {
        AuthState Current { get; }
        event EventHandler<AuthState> AuthStateChanged;

        Task BeginPkceLoginAsync();
        Task ExchangePkceCodeAsync(string code);
        Task RefreshAsync();
        Task<string> GetAccessToken();
    }
}