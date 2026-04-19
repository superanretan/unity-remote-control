using UnityEngine;
using SuperAnretan.RemoteControl;

namespace SuperAnretan.RemoteControl.Samples
{
    /// <summary>
    /// Handler dla komendy "set_color".
    /// Oczekuje w polu value hex koloru np. "#FF0000".
    /// Zmienia kolor materiału na docelowym Rendererze.
    /// </summary>
    public class SetColorHandler : CommandHandlerBase
    {
        public override string CommandType => "set_color";

        public override void Handle(RemoteCommand command, GameObject target)
        {
            var renderer = target.GetComponent<Renderer>();
            if (renderer == null)
            {
                Debug.LogWarning($"[SetColorHandler] Brak Renderer na targecie '{command.targetId}'.", target);
                return;
            }

            if (ColorUtility.TryParseHtmlString(command.value, out var color))
            {
                renderer.material.color = color;
            }
            else
            {
                Debug.LogWarning($"[SetColorHandler] Nieprawidłowy kolor: '{command.value}'.");
            }
        }
    }
}
