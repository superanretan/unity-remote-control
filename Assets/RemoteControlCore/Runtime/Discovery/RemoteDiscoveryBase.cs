using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    public abstract class RemoteDiscoveryBase : MonoBehaviour
    {
        [Header("Logging")]
        [SerializeField] protected StringEventChannel _logChannel;

        protected readonly List<DiscoveredDevice> _devices = new();

        // Both raised on the main thread.
        public event Action<IReadOnlyList<DiscoveredDevice>> DevicesChanged;
        public event Action<string> StatusChanged;

        public IReadOnlyList<DiscoveredDevice> Devices => _devices;
        public abstract bool IsSearching { get; }

        public abstract void Refresh();

        protected void SetDevices(IEnumerable<DiscoveredDevice> devices)
        {
            _devices.Clear();
            if (devices != null) _devices.AddRange(devices);
            DevicesChanged?.Invoke(_devices);
        }

        protected void SetStatus(string status)
        {
            StatusChanged?.Invoke(status);
        }

        protected void Log(string message)
        {
            _logChannel?.Raise(message);
        }
    }
}
