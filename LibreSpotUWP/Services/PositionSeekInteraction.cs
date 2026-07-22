namespace LibreSpotUWP.Services
{
    /// <summary>
    /// Converts one slider drag into one seek commit. Programmatic Value changes
    /// never begin an interaction, and release/capture-loss cannot both commit.
    /// </summary>
    public sealed class PositionSeekInteraction
    {
        public bool IsDragging { get; private set; }

        public void BeginDrag()
        {
            IsDragging = true;
        }

        public bool TryCommit(uint positionMs, out uint committedPositionMs)
        {
            if (!IsDragging)
            {
                committedPositionMs = 0;
                return false;
            }

            IsDragging = false;
            committedPositionMs = positionMs;
            return true;
        }

        public void Cancel()
        {
            IsDragging = false;
        }
    }
}
