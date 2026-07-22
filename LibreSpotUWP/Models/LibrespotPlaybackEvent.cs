namespace LibreSpotUWP.Models
{
    public sealed class LibrespotPlaybackEvent
    {
        public LibrespotPlaybackState State { get; set; }
        public ulong PlayRequestId { get; set; }
        public ulong AudioGeneration { get; set; }
        public long SessionGeneration { get; set; }
        public string TrackUri { get; set; }
        public uint PositionMs { get; set; }
        public bool IsSeek { get; set; }
    }
}
