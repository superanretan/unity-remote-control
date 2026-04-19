using UnityEngine;

namespace SuperAnretan.RemoteControl.Samples
{
    /// <summary>
    /// Handler for "set_color" commands.
    /// Expects <see cref="RemoteCommand.value"/> to be a hex color string (e.g. "#FF0000").
    /// Changes the target Renderer's material color.
    /// </summary>
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
