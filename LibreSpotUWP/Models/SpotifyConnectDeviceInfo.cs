namespace LibreSpotUWP.Models
{
    public sealed class SpotifyConnectDeviceInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public bool IsActive { get; set; }
        public bool IsRestricted { get; set; }
        public bool SupportsVolume { get; set; }
        public int? VolumePercent { get; set; }
        public bool IsThisDevice { get; set; }

        public string DisplayName => IsThisDevice ? "This device" : Name;
    }
}
