using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SuperAnretan.RemoteControl
{
    // Tab bar whose active tab is host-owned state rather than local UI state: clicking a tab sends
    // a command, and which tab shows is driven by the host's tab topic. That is what makes the
    // controller and the host agree instead of drifting the moment either side is touched.
    //
    // Pair it with one RemoteListView per tab, each filtering the same roster topic by its group.
    [DisallowMultipleComponent]
    [AddComponentMenu("Remote Control/Remote Tab Bar")]
    public class RemoteTabBar : MonoBehaviour
    {
        [Serializable]
        public class Tab
        {
            [Tooltip("Wire value sent to the host and expected back on the tab topic, e.g. \"workers\".")]
            public string value;

            public Button button;

            [Tooltip("Shown while this tab is active, hidden otherwise. Usually the tab's RemoteListView root.")]
            public GameObject content;

            [Tooltip("Tinted to show which tab is active. Empty => the button's Target Graphic.")]
            public Graphic graphic;
        }

        [Header("Sources")]
        [SerializeField] private HostStateRouter _stateRouter;
        [SerializeField] private CommandEventChannel _commandSendChannel;

        [Header("Topic and command (must match the host)")]
        [SerializeField] private string _tabTopic = "tab";
        [SerializeField] private string _setTabCommandType = "set_tab";
        [SerializeField] private string _targetId = "app";

        [Header("Tabs")]
        [SerializeField] private Tab[] _tabs = Array.Empty<Tab>();

        [Tooltip("Shown before the host reports a tab. Must match one of the values above.")]
        [SerializeField] private string _defaultValue = "";

        [Header("Colours")]
        [SerializeField] private Color _selectedColor = Color.white;
        [SerializeField] private Color _unselectedColor = new Color(0.5f, 0.5f, 0.5f, 1f);

        [Tooltip("Force Transition to None so the tint is not overwritten by the button's own hover/press colours.")]
        [SerializeField] private bool _overrideButtonTransition = true;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        // Kept so OnDisable can remove exactly the listeners this component added — RemoveAllListeners
        // would also wipe anything a designer wired on the button in the Inspector.
        private readonly List<(Button button, UnityAction handler)> _handlers = new();

        private string _active = string.Empty;

        public string ActiveValue => _active;

        public event Action<string> ActiveTabChanged;

        private void OnEnable()
        {
            for (int i = 0; i < _tabs.Length; i++)
            {
                Tab tab = _tabs[i];
                if (tab == null || tab.button == null) continue;

                if (tab.graphic == null) tab.graphic = tab.button.targetGraphic;
                if (_overrideButtonTransition) tab.button.transition = Selectable.Transition.None;

                string value = tab.value;
                UnityAction handler = () => OnTabClicked(value);
                tab.button.onClick.AddListener(handler);
                _handlers.Add((tab.button, handler));
            }

            if (_stateRouter != null) _stateRouter.StateChanged += OnStateChanged;

            // Prefer what the host already reported; otherwise show the default until it does.
            string initial = _stateRouter != null ? _stateRouter.GetValue(_tabTopic, _defaultValue) : _defaultValue;
            Render(string.IsNullOrEmpty(initial) ? FirstValue() : initial);
        }

        private void OnDisable()
        {
            for (int i = 0; i < _handlers.Count; i++)
                if (_handlers[i].button != null) _handlers[i].button.onClick.RemoveListener(_handlers[i].handler);
            _handlers.Clear();

            if (_stateRouter != null) _stateRouter.StateChanged -= OnStateChanged;
        }

        private void OnStateChanged(string topic, string value, string payload)
        {
            if (topic != _tabTopic) return;
            if (string.IsNullOrEmpty(value)) return;

            if (!Has(value))
            {
                Log($"[RemoteTabs] Host reported an unknown tab '{value}'.");
                return;
            }

            Render(value);
        }

        private void OnTabClicked(string value)
        {
            if (_commandSendChannel == null)
            {
                Debug.LogWarning($"[RemoteTabBar] CommandSendChannel not assigned on '{name}'.", this);
                return;
            }

            _commandSendChannel.Raise(new RemoteCommand(_setTabCommandType, _targetId, value));

            // Optimistic: the host's echo on the tab topic is still what settles it.
            Render(value);
        }

        private void Render(string value)
        {
            bool changed = _active != value;
            _active = value ?? string.Empty;

            for (int i = 0; i < _tabs.Length; i++)
            {
                Tab tab = _tabs[i];
                if (tab == null) continue;

                bool isActive = tab.value == _active;
                if (tab.content != null) tab.content.SetActive(isActive);
                if (tab.graphic != null) tab.graphic.color = isActive ? _selectedColor : _unselectedColor;
            }

            if (!changed) return;

            try { ActiveTabChanged?.Invoke(_active); }
            catch (Exception e) { Log($"[RemoteTabs] ActiveTabChanged subscriber threw: {e.Message}"); }
        }

        private bool Has(string value)
        {
            for (int i = 0; i < _tabs.Length; i++)
                if (_tabs[i] != null && _tabs[i].value == value) return true;
            return false;
        }

        private string FirstValue() => _tabs.Length > 0 && _tabs[0] != null ? _tabs[0].value : string.Empty;

        private void Log(string message) => _logChannel?.Raise(message);
    }
}
