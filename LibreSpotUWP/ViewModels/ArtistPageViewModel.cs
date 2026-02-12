using LibreSpotUWP;
using LibreSpotUWP.Interfaces;
using SpotifyAPI.Web;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public class ArtistPageViewModel
{
    private readonly ISpotifyWebService _web = App.SpotifyWeb;

    public FullArtist Artist { get; private set; }
    public Paging<SimpleAlbum> Albums { get; private set; }
    public List<FullTrack> TopTracks { get; private set; }

    public async Task LoadAsync(string id)
    {
        Artist = (await _web.GetArtistAsync(id)).Value;
        Albums = (await _web.GetArtistAlbumsAsync(id)).Value;

        var firstAlbum = Albums.Items.FirstOrDefault();
        if (firstAlbum != null)
        {
            var simpleTracks = (await _web.GetAlbumTracksAsync(firstAlbum.Id)).Value.Items;
            var fullTracks = new List<FullTrack>();

            foreach (var t in simpleTracks.Take(5))
            {
                if (!string.IsNullOrEmpty(t.Id))
                {
                    var full = await _web.GetTrackAsync(t.Id);
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
    }
}