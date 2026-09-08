using System;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    [CreateAssetMenu(
        fileName = "NewCommandEventChannel",
        menuName = "Remote Control/Events/Command Event Channel")]
    public class CommandEventChannel : ScriptableObject
    {
        public event Action<RemoteCommand> OnRaised;

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
