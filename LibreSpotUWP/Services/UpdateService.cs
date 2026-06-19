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
                Version bestVersion = null;
                UpdateInfo bestUpdate = null;

                foreach (var item in releases)
                {
                    var obj = item.GetObject();

                    bool isDraft = GetBoolean(obj, "draft");
                    bool isPrerelease = GetBoolean(obj, "prerelease");

                    if (isDraft || isPrerelease)
                        continue;

                    if (!TryGetString(obj, "tag_name", out string tagName))
                        continue;

                    string cleanVer = tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tagName.Substring(1) : tagName;

                    Debug.WriteLine($"Found release: {tagName}, draft={isDraft}, prerelease={isPrerelease}");

                    if (Version.TryParse(cleanVer, out Version remoteVer))
                    {
                        if (remoteVer.Major == localMajor)
                        {
                            if (IsNewer(remoteVer, currentVer))
                            {
                                if (bestVersion == null || IsNewer(remoteVer, bestVersion))
                                {
                                    bestVersion = remoteVer;
                                    bestUpdate = new UpdateInfo
                                    {
                                        IsUpdateAvailable = true,
                                        LatestVersion = tagName,
                                        ReleaseUrl = TryGetString(obj, "html_url", out string releaseUrl) ? releaseUrl : "",
                                        Body = TryGetString(obj, "body", out string body) ? body : ""
                                    };
                                }
                            }
                        }
                    }
                }

                if (bestUpdate != null)
                    return bestUpdate;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ParseReleases failed: {ex.Message}");
                LogService.Warn($"Update release parsing failed: {ex.Message}");
            }

            return new UpdateInfo { IsUpdateAvailable = false };
        }

        private static bool TryGetString(JsonObject obj, string key, out string value)
        {
            value = null;

            if (obj == null || !obj.ContainsKey(key))
                return false;

            try
            {
                value = obj[key].GetString();
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }

        private static bool GetBoolean(JsonObject obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key))
                return false;

            try
            {
                return obj[key].GetBoolean();
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNewer(Version remote, PackageVersion local)
        {
            if (CompareVersionPart(remote.Major, local.Major) > 0) return true;
            if (CompareVersionPart(remote.Major, local.Major) < 0) return false;

            if (CompareVersionPart(remote.Minor, local.Minor) > 0) return true;
            if (CompareVersionPart(remote.Minor, local.Minor) < 0) return false;

            if (CompareVersionPart(remote.Build, local.Build) > 0) return true;
            if (CompareVersionPart(remote.Build, local.Build) < 0) return false;

            return CompareVersionPart(remote.Revision, local.Revision) > 0;
        }

        private static bool IsNewer(Version remote, Version local)
        {
            if (CompareVersionPart(remote.Major, local.Major) > 0) return true;
            if (CompareVersionPart(remote.Major, local.Major) < 0) return false;

            if (CompareVersionPart(remote.Minor, local.Minor) > 0) return true;
            if (CompareVersionPart(remote.Minor, local.Minor) < 0) return false;

            if (CompareVersionPart(remote.Build, local.Build) > 0) return true;
            if (CompareVersionPart(remote.Build, local.Build) < 0) return false;

            return CompareVersionPart(remote.Revision, local.Revision) > 0;
        }

        private static int CompareVersionPart(int remotePart, int localPart)
        {
            return NormalizeVersionPart(remotePart).CompareTo(NormalizeVersionPart(localPart));
        }

        private static int NormalizeVersionPart(int part)
        {
            return Math.Max(part, 0);
        }
    }
}
