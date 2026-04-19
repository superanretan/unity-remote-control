using UnityEngine;

namespace SuperAnretan.RemoteControl.Samples
{
    /// <summary>
    /// Bootstrap for the Host demo scene.
    /// Logs the local IP address on start so the user knows where to connect.
    /// </summary>
    public class DemoHostBootstrap : MonoBehaviour
    {
        [Header("Logging")]
        [SerializeField] private StringEventChannel logChannel;

        private void Start()
        {
            var localIp = NetworkUtility.GetLocalIPAddress();
            logChannel?.Raise($"=== HOST READY ===");
            logChannel?.Raise($"Local IP: {localIp}");
            logChannel?.Raise($"Waiting for controller to connect...");
        }
    }
}
