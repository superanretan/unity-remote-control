using System.Collections.Generic;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Runtime registry of all active <see cref="ICommandHandler"/> instances.
    /// Handlers register on enable and unregister on disable.
    /// This is a ScriptableObject — create one asset and share it across components.
    /// </summary>
    [CreateAssetMenu(
        fileName = "HandlerRegistry",
        menuName = "Remote Control/Registries/Handler Registry")]
    public class CommandHandlerRegistry : ScriptableObject
    {
        private readonly Dictionary<string, ICommandHandler> _handlers = new();

        /// <summary>
        /// Register a handler. Warns on duplicate commandType.
        /// </summary>
        public void Register(ICommandHandler handler)
        {
            if (handler == null || string.IsNullOrEmpty(handler.CommandType))
            {
                Debug.LogWarning("[HandlerRegistry] Cannot register null handler or empty CommandType.");
                return;
            }

            if (_handlers.ContainsKey(handler.CommandType))
            {
                Debug.LogWarning(
                    $"[HandlerRegistry] Duplicate CommandType '{handler.CommandType}'. " +
                    "Overwriting previous handler.");
            }

            _handlers[handler.CommandType] = handler;
        }

        /// <summary>
        /// Unregister a handler.
        /// </summary>
        public void Unregister(ICommandHandler handler)
        {
            if (handler == null || string.IsNullOrEmpty(handler.CommandType)) return;

            if (_handlers.TryGetValue(handler.CommandType, out var existing) && existing == handler)
            {
                _handlers.Remove(handler.CommandType);
            }
        }

        /// <summary>
        /// Try to find a handler for a given command type.
        /// </summary>
        public bool TryGetHandler(string commandType, out ICommandHandler handler)
        {
            return _handlers.TryGetValue(commandType, out handler) && handler != null;
        }

        /// <summary>
        /// Current number of registered handlers.
        /// </summary>
        public int Count => _handlers.Count;

        /// <summary>
        /// Clear all registrations.
        /// </summary>
        public void Clear()
        {
            _handlers.Clear();
        }

        private void OnDisable()
        {
            _handlers.Clear();
        }
    }
}
