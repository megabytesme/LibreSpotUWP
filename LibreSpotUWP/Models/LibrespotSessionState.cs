using System;

namespace LibreSpotUWP.Models
{
    public sealed class LibrespotSessionState
    {
        public bool IsConnected { get; set; }
        public string UserName { get; set; }
        public bool AuthNeeded { get; set; }

        public LibrespotSessionState Clone()
        {
            return new LibrespotSessionState
            {
                IsConnected = this.IsConnected,
                UserName = this.UserName,
                AuthNeeded = this.AuthNeeded
            };
        }
    }
}