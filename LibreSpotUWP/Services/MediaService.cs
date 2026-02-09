using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media;

namespace LibreSpotUWP.Services
{
    public sealed class MediaService : IMediaService
    {
        private readonly ILibrespotService _librespot;
        private readonly ISpotifyAuthService _auth;
        private readonly ISpotifyWebService _web;

        private readonly object _lock = new object();

        private MediaState _state = new MediaState();
        private SystemMediaTransportControls _smtc;

        public MediaState Current => _state;

        public event EventHandler<MediaState> MediaStateChanged;

        public MediaService(
            ILibrespotService librespot,
            ISpotifyAuthService auth,
            ISpotifyWebService web)
        {
            _librespot = librespot;
            _auth = auth;
            _web = web;
        }

        public async Task InitializeAsync()
        {
            _smtc = SystemMediaTransportControls.GetForCurrentView();

            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsStopEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;

            _smtc.ButtonPressed += OnSmtcButtonPressed;

            _librespot.TrackChanged += OnTrackChanged;
            _librespot.PlaybackStateChanged += OnPlaybackChanged;
            _librespot.VolumeChanged += OnVolumeChanged;
            _auth.AuthStateChanged += OnAuthChanged;

            await Task.CompletedTask;
        }

        public async Task PlayTrackAsync(string spotifyUri)
        {
            var auth = _auth.Current;
            if (auth == null || auth.IsExpired)
            {
                await _auth.BeginPkceLoginAsync();
                return;
            }

            await _librespot.ConnectWithAccessTokenAsync(auth.AccessToken);
            await _librespot.LoadAndPlayAsync(spotifyUri);
        }

        public Task PauseAsync() => _librespot.PauseAsync();
        public Task ResumeAsync() => _librespot.ResumeAsync();
        public Task StopAsync() => _librespot.StopAsync();
        public Task SetVolumeAsync(ushort v) => _librespot.SetVolumeAsync(v);
        public void Next() => _librespot.Next();
        public void Previous() => _librespot.Previous();
        public void Seek(uint posMs) => _librespot.Seek(posMs);

        private async void OnTrackChanged(object sender, LibrespotTrackInfo track)
        {
            FullTrack metadata = null;

            if (!string.IsNullOrWhiteSpace(track.Uri))
            {
                var id = track.Uri.Replace("spotify:track:", "");
                metadata = await _web.GetTrackAsync(id);
            }

            UpdateState(state =>
            {
                state.Track = track;
                state.Metadata = metadata;
            });

            UpdateSmtcDisplay();
        }

        private void OnPlaybackChanged(object sender, LibrespotPlaybackState state)
        {
            UpdateState(s => s.PlaybackState = state);
            UpdateSmtcPlaybackStatus();
        }

        private void OnVolumeChanged(object sender, ushort volume)
        {
            UpdateState(s => s.Volume = volume);
        }

        private void OnAuthChanged(object sender, AuthState auth)
        {
            if (!auth.IsExpired)
                _ = _librespot.ConnectWithAccessTokenAsync(auth.AccessToken);
        }

        private void UpdateState(Action<MediaState> mutator)
        {
            MediaState snapshot;
            lock (_lock)
            {
                var clone = _state.Clone();
                mutator(clone);
                _state = clone;
                snapshot = clone;
            }

            MediaStateChanged?.Invoke(this, snapshot);
        }

        private void UpdateSmtcPlaybackStatus()
        {
            if (_smtc == null) return;

            switch (_state.PlaybackState)
            {
                case LibrespotPlaybackState.Playing:
                    _smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
                    break;
                case LibrespotPlaybackState.Paused:
                    _smtc.PlaybackStatus = MediaPlaybackStatus.Paused;
                    break;
                default:
                    _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
                    break;
            }
        }

        private void UpdateSmtcDisplay()
        {
            if (_smtc == null) return;

            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;

            var t = _state.Metadata;
            if (t != null)
            {
                updater.MusicProperties.Title = t.Name;
                updater.MusicProperties.Artist = string.Join(", ", t.Artists?.Select(a => a.Name));
                updater.MusicProperties.AlbumTitle = t.Album?.Name;

                if (t.Album?.Images != null && t.Album.Images.Any())
                {
                    var imageUrl = t.Album.Images[0].Url;
                    updater.Thumbnail = Windows.Storage.Streams.RandomAccessStreamReference.CreateFromUri(new Uri(imageUrl));
                }
            }

            updater.Update();
        }

        private async void OnSmtcButtonPressed(
            SystemMediaTransportControls sender,
            SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    await ResumeAsync();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    await PauseAsync();
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    await StopAsync();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    Next();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    Previous();
                    break;
            }
        }
    }
}