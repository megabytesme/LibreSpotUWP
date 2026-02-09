using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface ISecureStorage
    {
        Task SaveAsync(string key, string value);
        Task<string> LoadAsync(string key);
        Task DeleteAsync(string key);
    }
}