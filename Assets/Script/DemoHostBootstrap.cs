using UnityEngine;
using SuperAnretan.RemoteControl;

namespace SuperAnretan.RemoteControl.Samples
{

    public class DemoHostBootstrap : MonoBehaviour
    {
        [Header("Logging")]
        [SerializeField] private StringEventChannel logChannel;
        [SerializeField] private NetworkConfig networkConfig;
        [SerializeField] private TMPro.TMP_Text _hostInfoText;

        private void Start()
        {
            var detectedIp = NetworkUtility.GetLocalIPAddress();
            var port = networkConfig != null ? networkConfig.Port.ToString() : "7777";
            
            string info = $"Twój adres IP: {detectedIp}\nPort: {port}";

            if (_hostInfoText != null)
            {
                _hostInfoText.text = info;
            }

            logChannel?.Raise("=== HOST GOTOWY ===");
            logChannel?.Raise(info);
            logChannel?.Raise("Czekam na połączenie kontrolera...");
        }
    }
}
