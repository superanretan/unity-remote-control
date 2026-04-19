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
        [SerializeField] private StringEventChannel _logChannel;

        private void Start()
        {
            var localIp = NetworkUtility.GetLocalIPAddress();
            _logChannel?.Raise($"=== HOST READY ===");
            _logChannel?.Raise($"Local IP: {localIp}");
            _logChannel?.Raise($"Waiting for controller to connect...");
        }
    }
}
