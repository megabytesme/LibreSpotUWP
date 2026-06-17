using System.Threading.Tasks;
using LibreSpotUWP.Interfaces;
using Windows.Security.Credentials;

namespace LibreSpotUWP.Services
{
    public class SecureStorage : ISecureStorage
    {
        private readonly PasswordVault _vault = new PasswordVault();
        private const string ResourceName = "LibreSpotUWP";

        public async Task SaveAsync(string key, string value)
        {
            await DeleteAsync(key);
            _vault.Add(new PasswordCredential(ResourceName, key, value));
        }

        public Task<string> LoadAsync(string key)
        {
            try
            {
                var credential = _vault.Retrieve(ResourceName, key);
                credential.RetrievePassword();
                return Task.FromResult(credential.Password);
            }
            catch
            {
                return Task.FromResult<string>(null);
            }
        }

        public Task DeleteAsync(string key)
        {
            try
            {
                var credential = _vault.Retrieve(ResourceName, key);
                _vault.Remove(credential);
            }
            catch { }

            return Task.CompletedTask;
        }
    }
}
