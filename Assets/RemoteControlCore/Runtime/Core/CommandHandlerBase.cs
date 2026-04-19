using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Abstract base MonoBehaviour for command handlers.
    /// Automatically registers/unregisters with the <see cref="CommandHandlerRegistry"/>
    /// on enable/disable. Subclass and implement <see cref="CommandType"/> and <see cref="Handle"/>.
    /// </summary>
    public abstract class CommandHandlerBase : MonoBehaviour, ICommandHandler
    {
        [Header("Registry")]
        [Tooltip("The handler registry SO this handler registers into at runtime.")]
        [SerializeField] private CommandHandlerRegistry handlerRegistry;

        /// <inheritdoc/>
        public abstract string CommandType { get; }

        /// <inheritdoc/>
        public abstract void Handle(RemoteCommand command, GameObject target);

        protected virtual void OnEnable()
        {
            if (handlerRegistry != null)
            {
                handlerRegistry.Register(this);
            }
            else
            {
                Debug.LogWarning($"[{GetType().Name}] HandlerRegistry not assigned.", this);
            }
        }

        protected virtual void OnDisable()
        {
            if (handlerRegistry != null)
            {
                handlerRegistry.Unregister(this);
            }
        }
    }
}
