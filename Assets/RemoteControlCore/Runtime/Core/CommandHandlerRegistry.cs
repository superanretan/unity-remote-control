using System.Collections.Generic;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    [CreateAssetMenu(
        fileName = "HandlerRegistry",
        menuName = "Remote Control/Registries/Handler Registry")]
    public class CommandHandlerRegistry : ScriptableObject
    {
        private readonly Dictionary<string, ICommandHandler> _handlers = new();

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

        public void Unregister(ICommandHandler handler)
        {
            if (handler == null || string.IsNullOrEmpty(handler.CommandType)) return;

            if (_handlers.TryGetValue(handler.CommandType, out var existing) && existing == handler)
            {
                _handlers.Remove(handler.CommandType);
            }
        }

        public bool TryGetHandler(string commandType, out ICommandHandler handler)
        {
            return _handlers.TryGetValue(commandType, out handler) && handler != null;
        }

        public int Count => _handlers.Count;

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
