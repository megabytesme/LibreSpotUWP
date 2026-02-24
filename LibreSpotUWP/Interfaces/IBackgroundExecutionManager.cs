using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface IBackgroundExecutionManager
    {
        Task<bool> RequestKeepAliveAsync();
        void StopKeepAlive();
    }
}