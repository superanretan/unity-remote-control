using System;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// ScriptableObject event channel with no payload.
    /// Used for connect/disconnect notifications, start/stop signals, etc.
    /// </summary>
    [CreateAssetMenu(
        fileName = "NewVoidEventChannel",
        menuName = "Remote Control/Events/Void Event Channel")]
    public class VoidEventChannel : ScriptableObject
    {
        /// <summary>
        /// Subscribe in OnEnable, unsubscribe in OnDisable.
        /// </summary>
        public event Action OnRaised;

        /// <summary>
        /// Broadcast to all listeners.
        /// </summary>
        public void Raise()
        {
            OnRaised?.Invoke();
        }
    }
}
