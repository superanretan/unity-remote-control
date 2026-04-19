using System;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// ScriptableObject event channel that carries a string payload.
    /// Used for log messages, connect requests (IP string), etc.
    /// </summary>
    [CreateAssetMenu(
        fileName = "NewStringEventChannel",
        menuName = "Remote Control/Events/String Event Channel")]
    public class StringEventChannel : ScriptableObject
    {
        /// <summary>
        /// Subscribe in OnEnable, unsubscribe in OnDisable.
        /// </summary>
        public event Action<string> OnRaised;

        /// <summary>
        /// Broadcast a string to all listeners.
        /// </summary>
        public void Raise(string value)
        {
            OnRaised?.Invoke(value);
        }
    }
}
