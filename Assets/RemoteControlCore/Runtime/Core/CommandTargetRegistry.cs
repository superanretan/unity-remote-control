using System.Collections.Generic;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    [CreateAssetMenu(
        fileName = "TargetRegistry",
        menuName = "Remote Control/Registries/Target Registry")]
    public class CommandTargetRegistry : ScriptableObject
    {
        private readonly Dictionary<string, CommandTarget> _targets = new();

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

        public void Unregister(CommandTarget target)
        {
            if (target == null || string.IsNullOrEmpty(target.TargetId)) return;

            if (_targets.TryGetValue(target.TargetId, out var existing) && existing == target)
            {
                _targets.Remove(target.TargetId);
            }
        }

        public bool TryGetTarget(string targetId, out CommandTarget target)
        {
            return _targets.TryGetValue(targetId, out target) && target != null;
        }

        public int Count => _targets.Count;

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
