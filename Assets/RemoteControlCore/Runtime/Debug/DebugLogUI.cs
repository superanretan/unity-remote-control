using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// On-screen debug log that listens to a <see cref="StringEventChannel"/>
    /// and displays messages in a TextMeshProUGUI component.
    /// </summary>
    public class DebugLogUI : MonoBehaviour
    {
        [Header("Event Channel")]
        [Tooltip("The string event channel to listen to for log messages.")]
        [SerializeField] private StringEventChannel logChannel;

        [Header("UI")]
        [Tooltip("TMP text component to display log lines.")]
        [SerializeField] private TextMeshProUGUI logText;

        [Header("Settings")]
        [Tooltip("Maximum number of log lines to keep on screen.")]
        [SerializeField] private int maxLines = 20;

        private readonly Queue<string> _lines = new();

        private void OnEnable()
        {
            if (logChannel != null)
            {
                logChannel.OnRaised += OnLogMessage;
            }
        }

        private void OnDisable()
        {
            if (logChannel != null)
            {
                logChannel.OnRaised -= OnLogMessage;
            }
        }

        private void OnLogMessage(string message)
        {
            string timestamped = $"[{System.DateTime.Now:HH:mm:ss}] {message}";

            _lines.Enqueue(timestamped);
            while (_lines.Count > maxLines)
            {
                _lines.Dequeue();
            }

            if (logText != null)
            {
                logText.text = string.Join("\n", _lines);
            }

            Debug.Log(timestamped);
        }

        /// <summary>
        /// Clear all displayed log lines.
        /// </summary>
        public void ClearLog()
        {
            _lines.Clear();
            if (logText != null)
            {
                logText.text = string.Empty;
            }
        }
    }
}
