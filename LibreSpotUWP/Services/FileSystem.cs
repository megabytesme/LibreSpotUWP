using LibreSpotUWP.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace LibreSpotUWP.Services
{
    public class FileSystem : IFileSystem
    {
        private readonly StorageFolder _root;

        public FileSystem()
        {
            _root = ApplicationData.Current.LocalFolder;
        }

        public string AppDataDirectory => _root.Path;

        public string Combine(params string[] parts)
        {
            var validParts = parts?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() ?? new string[0];
            return Path.Combine(validParts);
        }

        private string GetRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            string normalizedPath = path.Replace("/", "\\");
            string normalizedRoot = _root.Path.Replace("/", "\\");

            if (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = normalizedPath.Remove(0, normalizedRoot.Length);
            }

            return normalizedPath.TrimStart('\\');
        }

        private async Task<StorageFolder> GetTargetFolderAsync(string relativePath, bool create = false)
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath == ".") return _root;

            var parts = relativePath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            StorageFolder current = _root;

            foreach (var part in parts)
            {
                if (create)
                    current = await current.CreateFolderAsync(part, CreationCollisionOption.OpenIfExists);
                else
                    current = await current.GetFolderAsync(part);
            }
            return current;
        }

        public async Task<bool> FileExistsAsync(string path)
        {
            try
            {
                string rel = GetRelativePath(path);
                var folder = await GetTargetFolderAsync(Path.GetDirectoryName(rel));
                await folder.GetFileAsync(Path.GetFileName(rel));
                return true;
            }
            catch { return false; }
        }

        public async Task WriteTextAsync(string path, string content)
        {
            string rel = GetRelativePath(path);
            var folder = await GetTargetFolderAsync(Path.GetDirectoryName(rel), true);
            var file = await folder.CreateFileAsync(Path.GetFileName(rel), CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, content);
        }

        public async Task<string> ReadTextAsync(string path)
        {
            string rel = GetRelativePath(path);
            var folder = await GetTargetFolderAsync(Path.GetDirectoryName(rel));
            var file = await folder.GetFileAsync(Path.GetFileName(rel));
            return await FileIO.ReadTextAsync(file);
        }

        public async Task<IEnumerable<string>> GetFilesAsync(string folderPath)
        {
            try
            {
                var folder = await GetTargetFolderAsync(GetRelativePath(folderPath));
                var files = await folder.GetFilesAsync();
                return files.Select(f => f.Path);
            }
            catch { return Enumerable.Empty<string>(); }
        }

        public async Task<IEnumerable<string>> GetFoldersAsync(string folderPath)
        {
            try
            {
                var folder = await GetTargetFolderAsync(GetRelativePath(folderPath));
                var folders = await folder.GetFoldersAsync();
                return folders.Select(f => f.Path);
            }
            catch { return Enumerable.Empty<string>(); }
        }

        public async Task DeleteFileAsync(string path)
        {
            try
            {
                string rel = GetRelativePath(path);
                var folder = await GetTargetFolderAsync(Path.GetDirectoryName(rel));
                var file = await folder.GetFileAsync(Path.GetFileName(rel));
                await file.DeleteAsync();
            }
            catch { }
        }

        public async Task CreateFolderAsync(string folder)
            => await GetTargetFolderAsync(GetRelativePath(folder), true);

        public async Task DeleteFolderAsync(string folder, bool recursive = true)
        {
            try
            {
                string rel = GetRelativePath(folder);
                if (string.IsNullOrEmpty(rel)) return;

                var target = await GetTargetFolderAsync(rel);
                await target.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (FileNotFoundException) {  }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileSystem] DeleteFolder FAILED → {ex.Message}");
            }
        }
    }
}