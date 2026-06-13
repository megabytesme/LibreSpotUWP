using System;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface IAppShell
    {
        void NavigateTo(string pageTag, bool forceReload = false);
        void NavigateToAlbum(string id);
        void NavigateToArtist(string id);
        void NavigateToPlaylist(string id);
        void NavigateToUserProfile(string id);
        void SetCacheStatus(string tooltip, bool showRefreshButton, Func<Task> refreshAction);
        void ClearCacheStatus();
    }
}
