using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Attach to any scene GameObject to make it addressable by remote commands.
    /// Registers itself into the <see cref="CommandTargetRegistry"/> on enable.
    /// </summary>
    public class CommandTarget : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("Unique string identifier for this target. Must match the targetId in incoming commands.")]
        [SerializeField] private string targetId;

        [Header("Registry")]
        [Tooltip("The target registry SO this object registers into at runtime.")]
        [SerializeField] private CommandTargetRegistry registry;

        /// <summary>
        /// The unique identifier for this target.
        /// </summary>
        public string TargetId => targetId;

        private void OnEnable()
        {
            if (registry != null)
            {
                registry.Register(this);
            }
            else
            {
                Debug.LogWarning($"[CommandTarget] Registry not assigned on '{gameObject.name}'.", this);
            }
        }

        private void OnDisable()
        {
            if (registry != null)
            {
                registry.Unregister(this);
            }
        }
    }
}
