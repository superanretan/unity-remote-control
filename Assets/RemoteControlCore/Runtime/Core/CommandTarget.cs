using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    public class CommandTarget : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("Unique string identifier for this target. Must match the targetId in incoming commands.")]
        [SerializeField] private string _targetId;

        [Header("Registry")]
        [Tooltip("The target registry SO this object registers into at runtime.")]
        [SerializeField] private CommandTargetRegistry _registry;

        public string TargetId => _targetId;

        private void OnEnable()
        {
            if (_registry != null)
            {
                _registry.Register(this);
            }
            else
            {
                Debug.LogWarning($"[CommandTarget] Registry not assigned on '{gameObject.name}'.", this);
            }
        }

        private void OnDisable()
        {
            if (_registry != null)
            {
                _registry.Unregister(this);
            }
        }
    }
}
