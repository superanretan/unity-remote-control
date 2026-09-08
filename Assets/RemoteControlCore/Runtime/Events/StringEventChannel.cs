using System;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    [CreateAssetMenu(
        fileName = "NewStringEventChannel",
        menuName = "Remote Control/Events/String Event Channel")]
    public class StringEventChannel : ScriptableObject
    {
        public event Action<string> OnRaised;

        public void Raise(string value)
        {
            OnRaised?.Invoke(value);
        }
    }
}
