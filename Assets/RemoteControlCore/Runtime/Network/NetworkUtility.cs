using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    public static class NetworkUtility
    {
        public static string GetLocalIPAddress()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "127.0.0.1";
#else
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
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
#endif
        }
    }
}
