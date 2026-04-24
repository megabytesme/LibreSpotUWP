using LibreSpotUWP;
using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public class ArtistPageViewModel
{
    private readonly ISpotifyWebService _web = App.SpotifyWeb;

    public FullArtist Artist { get; private set; }
    public Paging<SimpleAlbum> Albums { get; private set; }
    public List<FullTrack> TopTracks { get; private set; }
    public string StatusMessage { get; private set; }
    public DateTimeOffset? CachedAt { get; private set; }

    public async Task LoadAsync(string id, bool forceRefresh = false)
    {
        try
        {
            var artistResponse = await _web.GetArtistAsync(id, forceRefresh);
            var albumResponse = await _web.GetArtistAlbumsAsync(id, forceRefresh);

            Artist = artistResponse.Value;
            Albums = albumResponse.Value;

            var firstAlbum = Albums.Items.FirstOrDefault();
            if (firstAlbum != null)
            {
                var simpleTracksResponse = await _web.GetAlbumTracksAsync(firstAlbum.Id, forceRefresh);
                var simpleTracks = simpleTracksResponse.Value.Items;
                var fullTracks = new List<FullTrack>();

                foreach (var t in simpleTracks.Take(5))
                {
                    if (!string.IsNullOrEmpty(t.Id))
                    {
                        var full = await _web.GetTrackAsync(t.Id, forceRefresh);
                        if (full?.Value != null)
                            fullTracks.Add(full.Value);
                    }
                }

                TopTracks = fullTracks;
            }
            else
            {
                TopTracks = new List<FullTrack>();
            }

            StatusMessage = BuildStatusMessage(artistResponse, albumResponse);
            CachedAt = GetCachedAt(artistResponse, albumResponse);
        }
        catch (Exception)
        {
            Artist = null;
            Albums = new Paging<SimpleAlbum> { Items = new List<SimpleAlbum>() };
            TopTracks = new List<FullTrack>();
            StatusMessage = ConnectivityHelper.HasInternetAccess()
                ? "This artist page could not be loaded right now."
                : "Offline. This artist page is not available because it has not been cached yet.";
            CachedAt = null;
        }
    }

    private static string BuildStatusMessage<T1, T2>(CacheResponse<T1> first, CacheResponse<T2> second)
    {
        var offline = !ConnectivityHelper.HasInternetAccess();
        if (first?.IsOfflineFallback == true || second?.IsOfflineFallback == true)
            return "Offline. Artist details are being shown from cache.";

        if (offline)
            return "Offline. This artist page is available because it was cached earlier.";

        if (first?.IsFromCache == true || second?.IsFromCache == true)
            return "Showing cached artist details.";

        return null;
    }

    private static DateTimeOffset? GetCachedAt<T1, T2>(CacheResponse<T1> first, CacheResponse<T2> second)
    {
        DateTimeOffset? best = null;

        if (first?.IsFromCache == true || first?.IsOfflineFallback == true)
            best = first.Timestamp;

        if (second?.IsFromCache == true || second?.IsOfflineFallback == true)
            best = !best.HasValue || second.Timestamp > best.Value ? second.Timestamp : best;

        return best;
    }
}
