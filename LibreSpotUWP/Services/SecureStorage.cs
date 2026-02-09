using LibreSpotUWP.Interfaces;
using System.Threading.Tasks;
using Windows.Security.Credentials;

namespace LibreSpotUWP.Services
{
    public class SecureStorage : ISecureStorage
    {
        private readonly PasswordVault _vault = new PasswordVault();
        private const string ResourceName = "DriveRPC";

        public Task SaveAsync(string key, string value)
        {
            _vault.Add(new PasswordCredential(ResourceName, key, value));
            return Task.CompletedTask;
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