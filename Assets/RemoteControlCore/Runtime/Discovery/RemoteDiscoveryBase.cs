using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Platform-independent discovery contract consumed by <see cref="NetworkDiscoveryPanel"/>.
    /// Implementations: <see cref="WebGLDiscoveryClient"/> (signaling server) — a UDP broadcast
    /// implementation for native builds can derive from this class as well.
    /// The UI never needs to know which backend is active.
    /// </summary>
    public abstract class RemoteDiscoveryBase : MonoBehaviour
    {
        [Header("Logging")]
        [SerializeField] protected StringEventChannel _logChannel;

        protected readonly List<DiscoveredDevice> _devices = new();

        /// <summary>Raised on the main thread whenever the device list changes.</summary>
        public event Action<IReadOnlyList<DiscoveredDevice>> DevicesChanged;

        /// <summary>Raised with a short human-readable status ("Searching...", "Signaling offline", ...).</summary>
        public event Action<string> StatusChanged;

        /// <summary>Current snapshot of discovered devices.</summary>
        public IReadOnlyList<DiscoveredDevice> Devices => _devices;

        /// <summary>True while the backend is actively looking for hosts.</summary>
        public abstract bool IsSearching { get; }

        /// <summary>Force a fresh lookup (re-query the server / re-broadcast).</summary>
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
