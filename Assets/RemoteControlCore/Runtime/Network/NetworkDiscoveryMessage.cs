using System;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    [Serializable]
    public class NetworkDiscoveryMessage
    {
        public string HostName;
        public string HostIP;
        public int Port;

        public string ToJson()
        {
            return JsonUtility.ToJson(this);
        }

        public static NetworkDiscoveryMessage FromJson(string json)
        {
            try
            {
                return JsonUtility.FromJson<NetworkDiscoveryMessage>(json);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
