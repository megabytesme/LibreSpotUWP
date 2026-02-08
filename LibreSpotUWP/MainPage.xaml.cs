using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Storage;
using Windows.System.Profile;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using static LibreSpotUWP.Librespot;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace LibreSpotUWP
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        IntPtr instance;
        LibrespotRingBufferPlayer _player;

        public MainPage()
        {
            this.InitializeComponent();
            this.Loaded += MainPage_Loaded;
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("=== LibreSpot UWP Test Starting ===");
            var probe = await AudioFormatProbe.ProbeAsync();
            Debug.WriteLine("Selected librespot format: " + probe.LibrespotFormat);

            IntPtr handle = NativeProbe.TryLoadLibreSpot();
            if (handle == IntPtr.Zero)
            {
                Debug.WriteLine("[Main] librespot.dll FAILED to load.");
                return;
            }

            Debug.WriteLine("[Main] librespot.dll loaded successfully.");

            string deviceType;
            switch (AnalyticsInfo.VersionInfo.DeviceFamily)
            {
                case "Windows.Desktop":
                    deviceType = "computer";
                    break;
                case "Windows.Mobile":
                    deviceType = "smartphone";
                    break;
                case "Windows.Xbox":
                    deviceType = "gameconsole";
                    break;
                default:
                    deviceType = "speaker";
                    break;
            }

            var cfg = new Librespot.LibrespotConfig
            {
                device_name = Marshal.StringToHGlobalAnsi(Environment.MachineName),
                device_type = Marshal.StringToHGlobalAnsi(deviceType),
                cache_dir = Marshal.StringToHGlobalAnsi(ApplicationData.Current.LocalFolder.Path),
                enable_discovery = false,
                enable_volume_normalisation = false,
                bitrate = Bitrate.B320,
                format = probe.LibrespotFormat,
                username = IntPtr.Zero,
                password = IntPtr.Zero,
                auth_blob = IntPtr.Zero,

                access_token = Marshal.StringToHGlobalAnsi("")
            };

            string ts() => DateTime.Now.ToString("HH:mm:ss");

            Librespot.LibrespotCallback cb = (evtPtr, ud) =>
            {
                var evt = Marshal.PtrToStructure<LibrespotEvent>(evtPtr);

                switch (evt.event_type)
                {
                    case EventType.LogMessage:
                        Debug.WriteLine($"{ts()} [LibreSpot] {Marshal.PtrToStringAnsi(evt.data.log_msg)}");
                        break;
                    case EventType.SessionConnected:
                        Debug.WriteLine($"{ts()} [LibreSpot] [Auth] Connected as {Marshal.PtrToStringAnsi(evt.data.session_user)}");
                        break;
                    case EventType.SessionDisconnected:
                        Debug.WriteLine($"{ts()} [LibreSpot] [Auth] Disconnected: {Marshal.PtrToStringAnsi(evt.data.session_user)}");
                        break;
                    case EventType.AuthNeeded:
                        Debug.WriteLine($"{ts()} [LibreSpot] [Auth] Token expired or missing");
                        break;
                    case EventType.TrackChanged:
                        var t = evt.data.track;
                        Debug.WriteLine(
                            $"{ts()} [LibreSpot] [Track] " +
                            $"{Marshal.PtrToStringAnsi(t.name)} – " +
                            $"{Marshal.PtrToStringAnsi(t.artist)} " +
                            $"({Marshal.PtrToStringAnsi(t.album)})"
                        );
                        break;
                    case EventType.PlaybackPaused:
                        Debug.WriteLine($"{ts()} [LibreSpot] [Playback] Paused");
                        break;
                    case EventType.PlaybackResumed:
                        Debug.WriteLine($"{ts()} [LibreSpot] [Playback] Resumed");
                        break;
                    case EventType.PlaybackStopped:
                        Debug.WriteLine($"{ts()} [LibreSpot] [Playback] Stopped");
                        break;
                    case EventType.VolumeChanged:
                        Debug.WriteLine($"{ts()} [LibreSpot] [Volume] {evt.data.volume}");
                        break;
                    case EventType.Panic:
                        Debug.WriteLine($"{ts()} [LibreSpot] [Panic] {Marshal.PtrToStringAnsi(evt.data.log_msg)}");
                        break;
                    default:
                        Debug.WriteLine($"{ts()} [LibreSpot] [Event] Unknown event");
                        break;
                }
            };

            Debug.WriteLine("[Main] Calling librespot_new…");

            instance = Librespot.librespot_new(cfg, cb, IntPtr.Zero);

            if (instance == IntPtr.Zero)
            {
                Debug.WriteLine("[Main] librespot_new returned NULL.");
                return;
            }

            Debug.WriteLine("[Main] Instance created successfully.");

            var uri = Marshal.StringToHGlobalAnsi("spotify:track:4uLU6hMCjMI75M1A2tKUQC");
            Debug.WriteLine("[Main] Sending librespot_load…");
            Librespot.librespot_load(instance, uri, true);
            Debug.WriteLine("[Main] librespot_load sent.");

            _player = new LibrespotRingBufferPlayer(probe.EncodingProperties);
            await _player.InitializeAsync();

            Debug.WriteLine("=== LibreSpot UWP Test Running ===");
        }
    }
}