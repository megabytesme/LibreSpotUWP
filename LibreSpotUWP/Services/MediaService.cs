using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.UI.Xaml;

namespace LibreSpotUWP.Services
{
    public sealed class MediaService : IMediaService
    {
        private readonly ILibrespotService _librespot;
        private readonly ISpotifyAuthService _auth;
        private readonly ISpotifyWebService _web;

        private readonly object _lock = new object();

        private MediaState _state = new MediaState();
        private MediaPlayer _mediaPlayer;
        private SystemMediaTransportControls _smtc;

        private LibrespotRingBufferPlayer _ringPlayer;

        private DispatcherTimer _positionTimer;
        private DispatcherTimer _volumeDebounceTimer;
        private ushort _pendingVolume;
        private bool _volumeDirty = false;

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
            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.AutoPlay = false;
            _mediaPlayer.AudioCategory = MediaPlayerAudioCategory.Media;

            var commandManager = _mediaPlayer.CommandManager;
            commandManager.IsEnabled = true;
            commandManager.PlayBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            commandManager.PauseBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            commandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            commandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;

            _smtc = _mediaPlayer.SystemMediaTransportControls;
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

            _mediaPlayer.Source = CreateSilentMediaSource();

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            _volumeDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _volumeDebounceTimer.Tick += VolumeDebounceTimer_Tick;
            _volumeDebounceTimer.Start();

            await Task.CompletedTask;
        }

        private void PositionTimer_Tick(object sender, object e)
        {
            if (_state.PlaybackState != LibrespotPlaybackState.Playing)
                return;

            uint pos = _librespot.GetPositionMs();

            UpdateState(s => s.PositionMs = pos);
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

            if (_ringPlayer == null)
            {
                var props = (_librespot as LibrespotService)?.EncodingProperties
                            ?? AudioEncodingProperties.CreatePcm(44100, 2, 16);

                _ringPlayer = new LibrespotRingBufferPlayer(props);
                await _ringPlayer.InitializeAsync();
            }

            if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                _mediaPlayer.Play();

            _ringPlayer.Start();
        }

        public async Task PauseAsync()
        {
            await _librespot.PauseAsync();
            _mediaPlayer.Pause();
            _ringPlayer?.Stop();
        }

        public async Task ResumeAsync()
        {
            await _librespot.ResumeAsync();
            _mediaPlayer.Play();
            _ringPlayer?.Start();
        }

        public async Task StopAsync()
        {
            await _librespot.StopAsync();
            _mediaPlayer.Pause();
            _ringPlayer?.Stop();
        }

        private void VolumeDebounceTimer_Tick(object sender, object e)
        {
            if (!_volumeDirty)
                return;

            _volumeDirty = false;
            _ = _librespot.SetVolumeAsync(_pendingVolume);
        }

        public void SetVolumeDebounced(double percent)
        {
            ushort raw = (ushort)(percent * 65535 / 100);
            _pendingVolume = raw;
            _volumeDirty = true;
        }

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
                var resp = await _web.GetTrackAsync(id, false);
                metadata = resp.Value;
            }

            UpdateState(state =>
            {
                state.Track = track;
                state.Metadata = metadata;
                state.DurationMs = (uint)track.Duration.TotalMilliseconds;
            });

            UpdateSmtcDisplay();

            if (_state.PlaybackState == LibrespotPlaybackState.Playing)
            {
                _mediaPlayer.Play();
                _ringPlayer?.Start();
            }
        }

        private void OnPlaybackChanged(object sender, LibrespotPlaybackState state)
        {
            UpdateState(s => s.PlaybackState = state);

            if (state == LibrespotPlaybackState.Playing)
            {
                if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                    _mediaPlayer.Play();

                _ringPlayer?.Start();
            }
            else
            {
                if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Paused)
                    _mediaPlayer.Pause();

                _ringPlayer?.Stop();
            }
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

        private void UpdateSmtcDisplay()
        {
            if (_smtc == null)
                return;

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
                    updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(imageUrl));
                }
            }

            updater.Update();
        }

        private async void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
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

        private MediaSource CreateSilentMediaSource()
        {
            var props = AudioEncodingProperties.CreatePcm(44100, 2, 16);
            var descriptor = new AudioStreamDescriptor(props);
            var mss = new MediaStreamSource(descriptor);

            mss.CanSeek = false;
            mss.Duration = TimeSpan.FromHours(24);
            mss.BufferTime = TimeSpan.FromSeconds(0);

            TimeSpan _currentTime = TimeSpan.Zero;

            mss.Starting += (s, e) =>
            {
                e.Request.SetActualStartPosition(TimeSpan.Zero);
                _currentTime = TimeSpan.Zero;
            };

            byte[] silentBuffer = null;
            IBuffer silentIBuffer = null;
            TimeSpan silentDuration = TimeSpan.FromMilliseconds(500);

            mss.SampleRequested += (s, e) =>
            {
                if (silentBuffer == null)
                {
                    int frameSize = (int)props.ChannelCount * ((int)props.BitsPerSample / 8);
                    int samples = (int)(props.SampleRate * (silentDuration.TotalMilliseconds / 1000.0));
                    int bytes = samples * frameSize;

                    silentBuffer = new byte[bytes];
                    silentIBuffer = silentBuffer.AsBuffer();
                }

                var sample = MediaStreamSample.CreateFromBuffer(
                    silentIBuffer,
                    _currentTime
                );

                sample.Duration = silentDuration;
                e.Request.Sample = sample;

                _currentTime += silentDuration;
            };

            return MediaSource.CreateFromMediaStreamSource(mss);
        }
    }
}