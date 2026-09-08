using System;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    [CreateAssetMenu(
        fileName = "NewVoidEventChannel",
        menuName = "Remote Control/Events/Void Event Channel")]
    public class VoidEventChannel : ScriptableObject
    {
        public event Action OnRaised;

        public void Raise()
        {
            OnRaised?.Invoke();
        }
    }
}
