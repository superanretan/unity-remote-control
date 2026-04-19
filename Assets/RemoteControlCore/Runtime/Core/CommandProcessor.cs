using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Listens to a <see cref="CommandEventChannel"/> for incoming commands,
    /// resolves the handler and target from their respective registries,
    /// and executes the command. Logs all activity through a <see cref="StringEventChannel"/>.
    /// </summary>
    public class CommandProcessor : MonoBehaviour
    {
        [Header("Event Channels")]
        [Tooltip("Channel raised by TransportHost when a command is received.")]
        [SerializeField] private CommandEventChannel _commandReceivedChannel;

        [Header("Registries")]
        [Tooltip("Registry of all active command handlers.")]
        [SerializeField] private CommandHandlerRegistry _handlerRegistry;

        [Tooltip("Registry of all active command targets.")]
        [SerializeField] private CommandTargetRegistry _targetRegistry;

        [Header("Logging")]
        [Tooltip("Channel for debug log messages.")]
        [SerializeField] private StringEventChannel _logChannel;

        private void OnEnable()
        {
            if (_commandReceivedChannel != null)
            {
                _commandReceivedChannel.OnRaised += ProcessCommand;
            }
        }

        private void OnDisable()
        {
            if (_commandReceivedChannel != null)
            {
                _commandReceivedChannel.OnRaised -= ProcessCommand;
            }
        }

        private void ProcessCommand(RemoteCommand command)
        {
            if (command == null)
            {
                Log("[WARN] Received null command.");
                return;
            }

            // --- Resolve handler ---
            if (!_handlerRegistry.TryGetHandler(command.commandType, out var handler))
            {
                Log($"[WARN] Unknown command type: '{command.commandType}' — no handler registered.");
                return;
            }

            // --- Resolve target ---
            if (!_targetRegistry.TryGetTarget(command.targetId, out var target))
            {
                Log($"[WARN] Target not found: '{command.targetId}'.");
                return;
            }

            // --- Execute ---
            try
            {
                handler.Handle(command, target.gameObject);
                Log($"[OK] Executed '{command.commandType}' on '{command.targetId}' (value={command.value})");
            }
            catch (System.Exception e)
            {
                Log($"[ERROR] Handler '{command.commandType}' threw: {e.Message}");
                Debug.LogException(e, this);
            }
        }

        private void Log(string message)
        {
            _logChannel?.Raise(message);
        }
    }
}
