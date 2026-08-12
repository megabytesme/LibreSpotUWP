using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace LibreSpotUWP.Helpers
{
    public static class AppResetHelper
    {
        public static async Task ResetAllAppDataAsync()
        {
            try
            {
                if (App.Media != null)
                    await App.Media.StopAsync();
            }
            catch
            {
            }

            try
            {
                if (App.Librespot != null)
                    await App.Librespot.DisconnectAsync();
            }
            catch
            {
            }

            try
            {
                if (App.SpotifyPlaybackAuth != null)
                    await App.SpotifyPlaybackAuth.ResetAsync();
            }
            catch
            {
            }

            try
            {
                if (App.SpotifyAuth != null)
                    await App.SpotifyAuth.ResetAuthStateAsync();
            }
            catch
            {
            }

            ApplicationData.Current.LocalSettings.Values.Clear();

            await ClearFolderContentsAsync(ApplicationData.Current.LocalFolder);
            await ClearFolderContentsAsync(ApplicationData.Current.LocalCacheFolder);
            await ClearFolderContentsAsync(ApplicationData.Current.TemporaryFolder);
        }

        private static async Task ClearFolderContentsAsync(StorageFolder folder)
        {
            if (folder == null)
                return;

            try
            {
                var files = await folder.GetFilesAsync();
                foreach (var file in files)
                {
                    try
                    {
                        await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    catch
                    {
                    }
                }

                var subfolders = await folder.GetFoldersAsync();
                foreach (var subfolder in subfolders)
                {
                    try
                    {
                        await subfolder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
    }
}
