using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    public interface ICommandHandler
    {
        string CommandType { get; }

        void Handle(RemoteCommand command, GameObject target);
    }
}
