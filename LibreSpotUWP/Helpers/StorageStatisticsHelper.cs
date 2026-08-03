using LibreSpotUWP.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace LibreSpotUWP.Helpers
{
    internal sealed class StorageFolderStatistics
    {
        public bool IsAvailable { get; set; }
        public long Bytes { get; set; }
        public int FileCount { get; set; }
    }

    internal static class StorageStatisticsHelper
    {
        public static async Task<StorageFolderStatistics> GetChildFolderStatsAsync(
            StorageFolder rootFolder,
            string folderName)
        {
            if (rootFolder == null || string.IsNullOrWhiteSpace(folderName))
                return new StorageFolderStatistics();

            StorageFolder folder;
            try
            {
                folder = await rootFolder.GetFolderAsync(folderName);
            }
            catch (FileNotFoundException)
            {
                return new StorageFolderStatistics { IsAvailable = true };
            }
            catch (Exception ex)
            {
                LogService.Warn(
                    $"[StorageStatisticsHelper.GetChildFolderStatsAsync] Unable to open {folderName}: {ex.Message}");
                return new StorageFolderStatistics();
            }

            var statistics = new StorageFolderStatistics { IsAvailable = true };
            await AccumulateFolderAsync(folder, statistics);
            return statistics;
        }

        private static async Task AccumulateFolderAsync(
            StorageFolder folder,
            StorageFolderStatistics statistics)
        {
            System.Collections.Generic.IReadOnlyList<IStorageItem> items;
            try
            {
                // Use the WinRT storage broker instead of System.IO enumeration.
                // Windows 10 Mobile can reject FileSystemEnumerator access to an
                // app-local folder even though the StorageFolder remains readable.
                items = await folder.GetItemsAsync();
            }
            catch (Exception ex)
            {
                LogService.Warn(
                    $"[StorageStatisticsHelper.AccumulateFolderAsync] Unable to enumerate {folder.Name}: {ex.Message}");
                return;
            }

            foreach (var item in items)
            {
                var childFolder = item as StorageFolder;
                if (childFolder != null)
                {
                    await AccumulateFolderAsync(childFolder, statistics);
                    continue;
                }

                var file = item as StorageFile;
                if (file == null)
                    continue;

                try
                {
                    var properties = await file.GetBasicPropertiesAsync();
                    var size = properties.Size > long.MaxValue
                        ? long.MaxValue
                        : (long)properties.Size;
                    statistics.Bytes = statistics.Bytes > long.MaxValue - size
                        ? long.MaxValue
                        : statistics.Bytes + size;
                    statistics.FileCount++;
                }
                catch (Exception ex)
                {
                    // Cache files may disappear while playback or cleanup is
                    // running. Keep the remaining count useful instead of
                    // marking both storage locations unavailable.
                    LogService.Warn(
                        $"[StorageStatisticsHelper.AccumulateFolderAsync] Unable to inspect {file.Name}: {ex.Message}");
                }
            }
        }
    }
}
