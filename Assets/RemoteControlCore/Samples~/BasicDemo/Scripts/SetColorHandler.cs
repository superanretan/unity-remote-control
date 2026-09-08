using UnityEngine;

namespace SuperAnretan.RemoteControl.Samples
{
    // Expects command.value to be a hex color, e.g. "#FF0000".
    public class SetColorHandler : CommandHandlerBase
    {
        public override string CommandType => "set_color";

        public override void Handle(RemoteCommand command, GameObject target)
        {
            var renderer = target.GetComponent<Renderer>();
            if (renderer == null)
            {
                Debug.LogWarning(
                    $"[SetColorHandler] No Renderer on target '{command.targetId}'.", target);
                return;
            }

            if (ColorUtility.TryParseHtmlString(command.value, out var color))
            {
                renderer.material.color = color;
            }
            else
            {
                Debug.LogWarning(
                    $"[SetColorHandler] Invalid color value: '{command.value}'.");
            }
        }
    }
}
