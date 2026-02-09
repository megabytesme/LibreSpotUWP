using System.Collections.Generic;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface IFileSystem
    {
        Task<bool> FileExistsAsync(string path);
        Task<string> ReadTextAsync(string path);
        Task WriteTextAsync(string path, string content);
        Task DeleteFileAsync(string path);
        Task<IEnumerable<string>> GetFilesAsync(string folder);
        Task<IEnumerable<string>> GetFoldersAsync(string folder);
        Task CreateFolderAsync(string folder);
        Task DeleteFolderAsync(string folder, bool recursive = true);
        string Combine(params string[] parts);
        string AppDataDirectory { get; }
    }
}