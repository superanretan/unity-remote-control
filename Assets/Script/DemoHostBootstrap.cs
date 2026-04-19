using UnityEngine;
using SuperAnretan.RemoteControl;

namespace SuperAnretan.RemoteControl.Samples
{
    /// <summary>
    /// Bootstrap dla sceny Host.
    /// Loguje lokalne IP na starcie żeby wiedzieć gdzie się podłączyć.
    /// </summary>
    public class DemoHostBootstrap : MonoBehaviour
    {
        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        private void Start()
        {
            var localIp = NetworkUtility.GetLocalIPAddress();
            _logChannel?.Raise("=== HOST GOTOWY ===");
            _logChannel?.Raise($"Lokalne IP: {localIp}");
            _logChannel?.Raise("Czekam na połączenie kontrolera...");
        }
    }
}
