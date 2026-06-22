using LibreSpotUWP.Helpers;
using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace LibreSpotUWP.Controls
{
    public static class TrackAddToFlyoutHelper
    {
        public const string AddToGlyph = "\uECC8";
        private const int PlaylistFlyoutPageSize = 20;

        private static readonly Dictionary<string, bool> LikedTracks =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, bool> PlaylistTrackMembership =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();

        public static bool TryGetCachedLikedState(FullTrack track, out bool liked)
        {
            liked = false;
            var id = GetTrackId(track);
            if (string.IsNullOrWhiteSpace(id))
                return false;

            lock (Gate)
            {
                return LikedTracks.TryGetValue(id, out liked);
            }
        }

        public static async Task<IReadOnlyDictionary<string, bool>> LoadLikedStatesAsync(IEnumerable<FullTrack> tracks)
        {
            var ids = (tracks ?? Enumerable.Empty<FullTrack>())
                .Select(GetTrackId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ids.Count == 0 || App.SpotifyWeb == null || !ConnectivityHelper.HasInternetAccess())
                return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var states = await App.SpotifyWeb.CheckTracksSavedAsync(ids);
                lock (Gate)
                {
                    foreach (var state in states)
                        LikedTracks[state.Key] = state.Value;
                }

                return states;
            }
            catch (Exception ex)
            {
                if (!ConnectivityHelper.HasInternetAccess())
                {
                    LogService.Warn($"[TrackAddToFlyoutHelper.LoadLikedStatesAsync] Skipped liked state refresh while offline: {ex.Message}");
                    return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                }

                LogService.Error(ex, "[TrackAddToFlyoutHelper.LoadLikedStatesAsync] Failed to load liked states.");
                return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public static Task UpdateTrackLikeVisualAsync(MediaState state, FontIcon icon, Button button)
        {
            return UpdateTrackLikeVisualAsync(CreateTrackFromMediaState(state), icon, button);
        }

        public static async Task UpdateTrackLikeVisualAsync(FullTrack track, FontIcon icon, Button button)
        {
            ApplyIconState(icon, false);

            var trackId = GetTrackId(track);
            if (button != null)
            {
                button.Tag = track?.Uri ?? trackId ?? string.Empty;
                button.IsEnabled = !string.IsNullOrWhiteSpace(trackId) && ConnectivityHelper.HasInternetAccess();
                ToolTipService.SetToolTip(button, "Add to liked songs");
            }

            if (string.IsNullOrWhiteSpace(trackId) || App.SpotifyWeb == null)
                return;

            if (TryGetCachedLikedState(track, out var cachedLiked))
            {
                ApplyIconState(icon, cachedLiked);
                if (button != null)
                    ToolTipService.SetToolTip(button, cachedLiked ? "Add to playlist" : "Add to liked songs");
                return;
            }

            if (!ConnectivityHelper.HasInternetAccess())
                return;

            try
            {
                var states = await App.SpotifyWeb.CheckTracksSavedAsync(new[] { trackId });
                if (!states.TryGetValue(trackId, out var liked))
                    liked = false;

                RememberLiked(trackId, liked);
                if (button != null && !string.Equals(button.Tag as string, track?.Uri ?? trackId, StringComparison.OrdinalIgnoreCase))
                    return;

                ApplyIconState(icon, liked);
                if (button != null)
                    ToolTipService.SetToolTip(button, liked ? "Add to playlist" : "Add to liked songs");
            }
            catch (Exception ex)
            {
                if (!ConnectivityHelper.HasInternetAccess())
                {
                    LogService.Warn($"[TrackAddToFlyoutHelper.UpdateTrackLikeVisualAsync] Skipped liked state refresh while offline: {ex.Message}");
                    return;
                }

                LogService.Error(ex, "[TrackAddToFlyoutHelper.UpdateTrackLikeVisualAsync] Failed to update track like visual.");
            }
        }

        public static Task HandleTrackAddToAsync(FrameworkElement anchor, MediaState state, FontIcon icon = null)
        {
            return HandleTrackAddToAsync(anchor, CreateTrackFromMediaState(state), icon);
        }

        public static async Task HandleTrackAddToAsync(FrameworkElement anchor, FullTrack track, FontIcon icon = null)
        {
            if (anchor == null || track == null || App.SpotifyWeb == null || !ConnectivityHelper.HasInternetAccess())
                return;

            var trackId = GetTrackId(track);
            var trackUri = GetTrackUri(track);
            if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(trackUri))
                return;

            try
            {
                var liked = TryGetCachedLikedState(track, out var cachedLiked)
                    ? cachedLiked
                    : (await App.SpotifyWeb.CheckTracksSavedAsync(new[] { trackId })).TryGetValue(trackId, out var checkedLiked) && checkedLiked;

                if (!liked)
                {
                    await App.SpotifyWeb.SetTracksSavedAsync(new[] { trackId }, true);
                    RememberLiked(trackId, true);
                    ApplyIconState(icon, true);
                    if (anchor is Button button)
                        ToolTipService.SetToolTip(button, "Add to playlist");
                    return;
                }

                await ShowPlaylistFlyoutAsync(anchor, trackId, trackUri, icon, 0);
                ApplyIconState(icon, true);
            }
            catch (Exception ex)
            {
                if (!ConnectivityHelper.HasInternetAccess())
                {
                    LogService.Warn($"[TrackAddToFlyoutHelper.HandleTrackAddToAsync] Skipped add-to action while offline: {ex.Message}");
                    return;
                }

                LogService.Error(ex, "[TrackAddToFlyoutHelper.HandleTrackAddToAsync] Failed to update track add-to state.");
            }
        }

        public static FullTrack CreateTrackFromMediaState(MediaState state)
        {
            if (state?.Metadata != null)
                return state.Metadata;

            if (state?.Track == null)
                return null;

            var id = SpotifyIdHelper.TrackUriToId(state.Track.Uri);
            return new FullTrack
            {
                Id = id,
                Uri = state.Track.Uri,
                Name = state.Track.Name,
                DurationMs = (int)Math.Min(int.MaxValue, state.Track.Duration.TotalMilliseconds)
            };
        }

        public static string GetTrackId(FullTrack track)
        {
            if (!string.IsNullOrWhiteSpace(track?.Id))
                return track.Id;

            return SpotifyIdHelper.TrackUriToId(track?.Uri);
        }

        public static string GetTrackUri(FullTrack track)
        {
            if (!string.IsNullOrWhiteSpace(track?.Uri))
                return track.Uri;

            var id = GetTrackId(track);
            return string.IsNullOrWhiteSpace(id) ? null : $"spotify:track:{id}";
        }

        public static void ApplyIconState(FontIcon icon, bool active)
        {
            if (icon == null)
                return;

            icon.Glyph = AddToGlyph;
            icon.Foreground = GetStateBrush(active);
        }

        public static Brush GetStateBrush(bool active)
        {
            return (Brush)Application.Current.Resources[active
                ? "SystemControlHighlightAccentBrush"
                : "SystemControlForegroundBaseHighBrush"];
        }

        private static async Task ShowPlaylistFlyoutAsync(
            FrameworkElement anchor,
            string trackId,
            string trackUri,
            FontIcon parentIcon,
            int offset)
        {
            var flyout = new MenuFlyout();
            var track = new FullTrack { Id = trackId, Uri = trackUri };
            var liked = TryGetCachedLikedState(track, out var cachedLiked) ? cachedLiked : true;
            var likedIcon = CreateFlyoutIcon(liked);
            var likedItem = new MenuFlyoutItem
            {
                Text = "Liked Songs",
                Icon = likedIcon
            };

            likedItem.Click += async (s, e) =>
            {
                likedItem.IsEnabled = false;
                try
                {
                    var nextLiked = !liked;
                    await App.SpotifyWeb.SetTracksSavedAsync(new[] { trackId }, nextLiked);
                    liked = nextLiked;
                    RememberLiked(trackId, liked);
                    likedIcon.Foreground = GetStateBrush(liked);
                    ApplyIconState(parentIcon, liked);
                }
                catch (Exception ex)
                {
                    LogService.Error(ex, "[TrackAddToFlyoutHelper.ShowPlaylistFlyoutAsync] Failed to update liked songs.");
                }
                finally
                {
                    likedItem.IsEnabled = true;
                }
            };

            flyout.Items.Add(likedItem);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var loadingItem = new MenuFlyoutItem { Text = "Loading playlists...", IsEnabled = false };
            flyout.Items.Add(loadingItem);
            flyout.ShowAt(anchor);

            try
            {
                var response = await App.SpotifyWeb.GetCurrentUserPlaylistsPageAsync(PlaylistFlyoutPageSize, offset, false);
                var playlists = response?.Value?.Items ?? new List<FullPlaylist>();
                var total = response?.Value?.Total ?? (offset + playlists.Count);

                flyout.Items.Remove(loadingItem);

                if (offset > 0)
                    flyout.Items.Add(CreatePageNavigationItem(anchor, trackId, trackUri, parentIcon, Math.Max(0, offset - PlaylistFlyoutPageSize), "Previous playlists"));

                if (playlists.Count == 0)
                {
                    flyout.Items.Add(new MenuFlyoutItem { Text = "No playlists found", IsEnabled = false });
                    return;
                }

                foreach (var playlist in playlists.Where(p => !string.IsNullOrWhiteSpace(p?.Id)))
                    flyout.Items.Add(CreatePlaylistItem(anchor, playlist, trackUri));

                if (offset + playlists.Count < total)
                    flyout.Items.Add(CreatePageNavigationItem(anchor, trackId, trackUri, parentIcon, offset + PlaylistFlyoutPageSize, "More playlists"));
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "[TrackAddToFlyoutHelper.ShowPlaylistFlyoutAsync] Failed to load playlists.");
                flyout.Items.Remove(loadingItem);
                flyout.Items.Add(new MenuFlyoutItem { Text = "Playlists could not be loaded", IsEnabled = false });
            }
        }

        private static MenuFlyoutItem CreatePageNavigationItem(
            FrameworkElement anchor,
            string trackId,
            string trackUri,
            FontIcon parentIcon,
            int offset,
            string text)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += async (s, e) =>
            {
                await Task.Delay(50);
                await ShowPlaylistFlyoutAsync(anchor, trackId, trackUri, parentIcon, offset);
            };

            return item;
        }

        private static MenuFlyoutItem CreatePlaylistItem(FrameworkElement anchor, FullPlaylist playlist, string trackUri)
        {
            var playlistId = playlist.Id;
            var playlistName = string.IsNullOrWhiteSpace(playlist.Name) ? "Untitled playlist" : playlist.Name;
            bool? included = TryGetCachedPlaylistMembership(playlistId, trackUri, out var cachedIncluded)
                ? cachedIncluded
                : (bool?)null;
            var icon = CreateFlyoutIcon(included == true);
            var item = new MenuFlyoutItem
            {
                Text = playlistName,
                Icon = icon
            };

            item.Click += async (s, e) =>
            {
                item.IsEnabled = false;
                await Task.Delay(50);
                var statusFlyout = ShowStatusFlyout(anchor, $"Checking {playlistName}...", out var statusText);
                try
                {
                    var isIncluded = included ?? await TryPlaylistContainsTrackAsync(playlistId, trackUri);
                    if (!isIncluded.HasValue)
                    {
                        statusText.Text = $"Could not check {playlistName}.";
                        await Task.Delay(1400);
                        return;
                    }

                    statusText.Text = isIncluded.Value
                        ? $"Removing from {playlistName}..."
                        : $"Adding to {playlistName}...";

                    if (isIncluded.Value)
                        await App.SpotifyWeb.RemoveTrackFromPlaylistAsync(playlistId, trackUri);
                    else
                        await App.SpotifyWeb.AddTrackToPlaylistAsync(playlistId, trackUri);

                    included = !isIncluded.Value;
                    RememberPlaylistMembership(playlistId, trackUri, included.Value);
                    icon.Foreground = GetStateBrush(included.Value);
                    statusText.Text = included.Value
                        ? $"Added to {playlistName}."
                        : $"Removed from {playlistName}.";
                    LogService.Info($"[TrackAddToFlyoutHelper.CreatePlaylistItem] {(included.Value ? "Added" : "Removed")} {trackUri} {(included.Value ? "to" : "from")} playlist {playlistId}.");
                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    LogService.Error(ex, "[TrackAddToFlyoutHelper.ShowPlaylistFlyoutAsync] Failed to update playlist membership.");
                    statusText.Text = $"Could not update {playlistName}.";
                    await Task.Delay(1600);
                }
                finally
                {
                    statusFlyout?.Hide();
                    item.IsEnabled = true;
                }
            };

            return item;
        }

        private static FontIcon CreateFlyoutIcon(bool active)
        {
            return new FontIcon
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Glyph = AddToGlyph,
                Foreground = GetStateBrush(active)
            };
        }

        private static Flyout ShowStatusFlyout(FrameworkElement anchor, string message, out TextBlock statusText)
        {
            statusText = new TextBlock
            {
                Text = message,
                Margin = new Thickness(14, 10, 14, 10),
                MaxWidth = 280,
                TextWrapping = TextWrapping.Wrap
            };

            var flyout = new Flyout
            {
                Content = statusText
            };

            try
            {
                flyout.ShowAt(anchor);
                return flyout;
            }
            catch (Exception ex)
            {
                LogService.Warn($"[TrackAddToFlyoutHelper.ShowStatusFlyout] Unable to show status flyout: {ex.Message}");
                return null;
            }
        }

        private static async Task<bool?> TryPlaylistContainsTrackAsync(string playlistId, string trackUri)
        {
            try
            {
                return await App.SpotifyWeb.PlaylistContainsTrackAsync(playlistId, trackUri);
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "[TrackAddToFlyoutHelper.TryPlaylistContainsTrackAsync] Failed to inspect playlist.");
                return null;
            }
        }

        private static void RememberLiked(string trackId, bool liked)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return;

            lock (Gate)
            {
                LikedTracks[trackId] = liked;
            }
        }

        private static bool TryGetCachedPlaylistMembership(string playlistId, string trackUri, out bool included)
        {
            included = false;
            var key = GetPlaylistTrackMembershipKey(playlistId, trackUri);
            if (key == null)
                return false;

            lock (Gate)
            {
                return PlaylistTrackMembership.TryGetValue(key, out included);
            }
        }

        private static void RememberPlaylistMembership(string playlistId, string trackUri, bool included)
        {
            var key = GetPlaylistTrackMembershipKey(playlistId, trackUri);
            if (key == null)
                return;

            lock (Gate)
            {
                PlaylistTrackMembership[key] = included;
            }
        }

        private static string GetPlaylistTrackMembershipKey(string playlistId, string trackUri)
        {
            if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(trackUri))
                return null;

            return $"{playlistId}|{trackUri}";
        }
    }
}
