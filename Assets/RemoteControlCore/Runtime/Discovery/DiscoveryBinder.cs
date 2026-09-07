using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Lets the <see cref="NetworkDiscoveryPanel"/> prefab stay backend-agnostic: at startup it
    /// looks up whichever <see cref="RemoteDiscoveryBase"/> lives in the scene
    /// (WebGLDiscoveryClient on WebGL, a UDP implementation on native) and hands it to the panel.
    /// If the panel already has an explicit reference nothing happens.
    /// </summary>
    [RequireComponent(typeof(NetworkDiscoveryPanel))]
    public class DiscoveryBinder : MonoBehaviour
    {
        [Tooltip("Optional explicit backend. Leave empty to auto-find the first RemoteDiscoveryBase in the scene.")]
        [SerializeField] private RemoteDiscoveryBase _discovery;

        private void Awake()
        {
            var panel = GetComponent<NetworkDiscoveryPanel>();
            if (panel.HasDiscovery) return;

            var backend = _discovery != null ? _discovery : FindFirstObjectByType<RemoteDiscoveryBase>(FindObjectsInactive.Exclude);
            if (backend == null)
            {
                Debug.LogWarning("[DiscoveryBinder] No RemoteDiscoveryBase found in scene — the device list will stay empty.", this);
                return;
            }

            panel.SetDiscovery(backend);
        }
    }
}
