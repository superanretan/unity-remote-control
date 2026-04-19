using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Interface for command handlers.
    /// Each handler is responsible for one <see cref="CommandType"/> string.
    /// </summary>
    public interface ICommandHandler
    {
        /// <summary>
        /// The string key this handler responds to — e.g. "set_color".
        /// Must be unique across all registered handlers.
        /// </summary>
        string CommandType { get; }

        /// <summary>
        /// Execute the command on the resolved target GameObject.
        /// </summary>
        /// <param name="command">The full deserialized command.</param>
        /// <param name="target">The resolved target GameObject from the registry.</param>
        void Handle(RemoteCommand command, GameObject target);
    }
}
