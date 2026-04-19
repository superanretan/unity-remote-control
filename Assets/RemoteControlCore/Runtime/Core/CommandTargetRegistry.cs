using System.Collections.Generic;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Runtime registry of all active <see cref="CommandTarget"/> instances.
    /// Targets register on enable and unregister on disable.
    /// This is a ScriptableObject — create one asset and share it across components.
    /// </summary>
    [CreateAssetMenu(
        fileName = "TargetRegistry",
        menuName = "Remote Control/Registries/Target Registry")]
    public class CommandTargetRegistry : ScriptableObject
    {
        private readonly Dictionary<string, CommandTarget> _targets = new();

        /// <summary>
        /// Register a target. Warns on duplicate targetId.
        /// </summary>
        public void Register(CommandTarget target)
        {
            if (target == null || string.IsNullOrEmpty(target.TargetId))
            {
                Debug.LogWarning("[TargetRegistry] Cannot register null target or empty targetId.");
                return;
            }

            if (_targets.ContainsKey(target.TargetId))
            {
                Debug.LogWarning(
                    $"[TargetRegistry] Duplicate targetId '{target.TargetId}' " +
                    $"from '{target.gameObject.name}'. Overwriting previous registration.");
            }

            _targets[target.TargetId] = target;
        }

        /// <summary>
        /// Unregister a target.
        /// </summary>
        public void Unregister(CommandTarget target)
        {
            if (target == null || string.IsNullOrEmpty(target.TargetId)) return;

            if (_targets.TryGetValue(target.TargetId, out var existing) && existing == target)
            {
                _targets.Remove(target.TargetId);
            }
        }

        /// <summary>
        /// Try to find a target by its string id.
        /// </summary>
        public bool TryGetTarget(string targetId, out CommandTarget target)
        {
            return _targets.TryGetValue(targetId, out target) && target != null;
        }

        /// <summary>
        /// Current number of registered targets.
        /// </summary>
        public int Count => _targets.Count;

        /// <summary>
        /// Clear all registrations. Called automatically on domain reload,
        /// but can be called manually for cleanup.
        /// </summary>
        public void Clear()
        {
            _targets.Clear();
        }

        private void OnDisable()
        {
            _targets.Clear();
        }
    }
}
