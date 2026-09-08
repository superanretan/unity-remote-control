using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    public abstract class CommandHandlerBase : MonoBehaviour, ICommandHandler
    {
        [Header("Registry")]
        [Tooltip("The handler registry SO this handler registers into at runtime.")]
        [SerializeField] private CommandHandlerRegistry _handlerRegistry;

        public abstract string CommandType { get; }

        public abstract void Handle(RemoteCommand command, GameObject target);

        protected virtual void OnEnable()
        {
            if (_handlerRegistry != null)
            {
                _handlerRegistry.Register(this);
            }
            else
            {
                Debug.LogWarning($"[{GetType().Name}] HandlerRegistry not assigned.", this);
            }
        }

        protected virtual void OnDisable()
        {
            if (_handlerRegistry != null)
            {
                _handlerRegistry.Unregister(this);
            }
        }
    }
}
