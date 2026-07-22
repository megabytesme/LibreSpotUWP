namespace LibreSpotUWP.Models
{
    public enum LibrespotPositionUpdateOrigin
    {
        SeekAcknowledgement,
        PositionCorrection,
        Progress
    }

    public struct LibrespotPositionUpdate
    {
        public uint PositionMs { get; set; }
        public LibrespotPositionUpdateOrigin Origin { get; set; }
        public long SessionGeneration { get; set; }
    }
}
