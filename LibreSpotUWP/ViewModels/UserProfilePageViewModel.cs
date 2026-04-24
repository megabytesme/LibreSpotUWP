using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LibreSpotUWP.ViewModels
{
    public class UserProfilePageViewModel
    {
        private readonly ILibrespotService _librespot = App.Librespot;

        public AppUserProfile Profile { get; private set; }
        public List<FullPlaylist> Playlists { get; private set; } = new List<FullPlaylist>();
        public string StatusMessage { get; private set; }

        public async Task LoadAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User id must not be empty.", nameof(userId));

            try
            {
                var profile = await _librespot.GetUserProfileAsync(userId);
                var playlists = await _librespot.GetUserPlaylistsAsync(userId);

                Profile = new AppUserProfile
                {
                    Id = profile?.Id,
                    Uri = profile?.Uri,
                    DisplayName = profile?.DisplayName,
                    Email = profile?.Email,
                    Country = profile?.Country,
                    Images = profile?.Images?
                        .Select(image => new AppImage
                        {
                            Url = image.Url,
                            Width = image.Width,
                            Height = image.Height
                        })
                        .ToList() ?? new List<AppImage>()
                };

                Playlists = playlists?.Items?
                    .Select(item => new FullPlaylist
                    {
                        Id = item.Id,
                        Uri = item.Uri,
                        Name = item.Name,
                        Images = item.Images?
                            .Select(image => new Image
                            {
                                Url = image.Url,
                                Width = image.Width,
                                Height = image.Height
                            })
                            .ToList() ?? new List<Image>()
                    })
                    .ToList() ?? new List<FullPlaylist>();

                StatusMessage = null;
            }
            catch (Exception)
            {
                Profile = null;
                Playlists = new List<FullPlaylist>();
                StatusMessage = "This user profile could not be loaded right now.";
            }
        }
    }
}
