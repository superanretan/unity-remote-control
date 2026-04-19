using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Static utility for network-related helpers.
    /// </summary>
    public static class NetworkUtility
    {
        /// <summary>
        /// Returns the first IPv4 address of this machine on the local network.
        /// Falls back to "127.0.0.1" if no network interface is found.
        /// </summary>
        public static string GetLocalIPAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[NetworkUtility] Could not resolve local IP: {e.Message}");
            }

            return "127.0.0.1";
        }
    }
}
