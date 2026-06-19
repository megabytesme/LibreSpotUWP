using System.Threading.Tasks;
using LibreSpotUWP.Interfaces;
using Windows.Security.Credentials;

namespace LibreSpotUWP.Services
{
    public class SecureStorage : ISecureStorage
    {
        private readonly PasswordVault _vault = new PasswordVault();
        private const string ResourceName = "LibreSpotUWP";
        private static readonly string[] LegacyResourceNames =
        {
            "DriveRPC"
        };

        public async Task SaveAsync(string key, string value)
        {
            await DeleteAsync(key);
            _vault.Add(new PasswordCredential(ResourceName, key, value));
        }

        public Task<string> LoadAsync(string key)
        {
            DeleteLegacyCredentials(key);

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
            DeleteCredentials(ResourceName, key);
            DeleteLegacyCredentials(key);

            return Task.CompletedTask;
        }

        private void DeleteLegacyCredentials(string key)
        {
            foreach (var resourceName in LegacyResourceNames)
                DeleteCredentials(resourceName, key);
        }

        private void DeleteCredentials(string resourceName, string key)
        {
            try
            {
                var credentials = _vault.FindAllByResource(resourceName);
                foreach (var credential in credentials)
                {
                    if (credential.UserName == key)
                        _vault.Remove(credential);
                }
            }
            catch
            {
                try
                {
                    var credential = _vault.Retrieve(resourceName, key);
                    _vault.Remove(credential);
                }
                catch
                {
                }
            }
        }
    }
}
