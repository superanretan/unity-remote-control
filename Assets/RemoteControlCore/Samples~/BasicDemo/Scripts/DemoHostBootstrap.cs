using UnityEngine;

namespace SuperAnretan.RemoteControl.Samples
{
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
