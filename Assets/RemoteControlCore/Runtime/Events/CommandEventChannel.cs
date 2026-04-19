using System;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// ScriptableObject event channel that carries a <see cref="RemoteCommand"/> payload.
    /// Used for command-received (Host) and command-send-request (Controller) flows.
    /// </summary>
    [CreateAssetMenu(
        fileName = "NewCommandEventChannel",
        menuName = "Remote Control/Events/Command Event Channel")]
    public class CommandEventChannel : ScriptableObject
    {
        /// <summary>
        /// Subscribe in OnEnable, unsubscribe in OnDisable.
        /// </summary>
        public event Action<RemoteCommand> OnRaised;

        /// <summary>
        /// Broadcast a command to all listeners.
        /// </summary>
        public void Raise(RemoteCommand command)
        {
            if (command == null)
            {
                Debug.LogWarning($"[{name}] Attempted to raise null command.");
                return;
            }
            OnRaised?.Invoke(command);
        }
    }
}
