using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Web.Http;

namespace LibreSpotUWP.Services
{
    public class UpdateInfo
    {
        public bool IsUpdateAvailable { get; set; }
        public string LatestVersion { get; set; }
        public string ReleaseUrl { get; set; }
        public string Body { get; set; }
    }

    public static class UpdateService
    {
        private const string RepoOwner = "megabytesme";
        private const string RepoName = "LibreSpotUWP";

        public static async Task<UpdateInfo> CheckForUpdatesAsync()
        {
#if STORE_BUILD
            return new UpdateInfo { IsUpdateAvailable = false };
#endif
            try
            {
                var filter = new Windows.Web.Http.Filters.HttpBaseProtocolFilter();
                using (var client = new HttpClient(filter))
                {
                    client.DefaultRequestHeaders.UserAgent.TryParseAdd("LibreSpotUWP-UWP");

                    string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases";

                    HttpResponseMessage response = await client.GetAsync(new Uri(url));

                    if (!response.IsSuccessStatusCode)
                    {
                        Debug.WriteLine($"Update Check Failed: HTTP {response.StatusCode}");
                        LogService.Warn($"Update check failed: HTTP {response.StatusCode}");
                        return new UpdateInfo { IsUpdateAvailable = false };
                    }

                    string json = await response.Content.ReadAsStringAsync();
                    return ParseReleases(json);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Update Check Failed: {ex.Message}");
                LogService.Warn($"Update check failed: {ex.Message}");
                return new UpdateInfo { IsUpdateAvailable = false };
            }
        }

        private static UpdateInfo ParseReleases(string json)
        {
            try
            {
                var releases = JsonArray.Parse(json);
                var currentVer = Package.Current.Id.Version;

                ushort localMajor = currentVer.Major;

                foreach (var item in releases)
                {
                    var obj = item.GetObject();

                    if (obj.ContainsKey("draft") && obj["draft"].GetBoolean())
                        continue;

                    string tagName = obj["tag_name"].GetString();
                    string cleanVer = tagName.StartsWith("v") ? tagName.Substring(1) : tagName;

                    Debug.WriteLine($"Found release: {tagName}, draft={obj["draft"].GetBoolean()}");

                    if (Version.TryParse(cleanVer, out Version remoteVer))
                    {
                        if (remoteVer.Major == localMajor)
                        {
                            if (IsNewer(remoteVer, currentVer))
                            {
                                return new UpdateInfo
                                {
                                    IsUpdateAvailable = true,
                                    LatestVersion = tagName,
                                    ReleaseUrl = obj["html_url"].GetString(),
                                    Body = obj.ContainsKey("body") ? obj["body"].GetString() : ""
                                };
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ParseReleases failed: {ex.Message}");
                LogService.Warn($"Update release parsing failed: {ex.Message}");
            }

            return new UpdateInfo { IsUpdateAvailable = false };
        }

        private static bool IsNewer(Version remote, PackageVersion local)
        {
            if (remote.Major > local.Major) return true;
            if (remote.Major < local.Major) return false;

            if (remote.Minor > local.Minor) return true;
            if (remote.Minor < local.Minor) return false;

            if (remote.Build > local.Build) return true;
            if (remote.Build < local.Build) return false;

            if (remote.Revision > local.Revision) return true;

            return false;
        }
    }
}
