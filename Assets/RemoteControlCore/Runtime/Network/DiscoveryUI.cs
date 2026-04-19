using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Manages the UI for network discovery.
    /// Needs a reference to NetworkDiscoveryClient (can be found in scene).
    /// Updates a TMP_Dropdown with discovered hosts.
    /// </summary>
    public class DiscoveryUI : MonoBehaviour
    {
        [SerializeField] private NetworkDiscoveryClient discoveryClient;
        [SerializeField] private TMP_Dropdown hostDropdown;
        [SerializeField] private Button refreshButton;
        
        [Tooltip("SO Channel triggered when a host is selected from the dropdown.")]
        [SerializeField] private StringEventChannel onHostSelectedChannel;

        private List<NetworkDiscoveryMessage> _currentHosts = new List<NetworkDiscoveryMessage>();

        private void OnEnable()
        {
            if (refreshButton != null)
                refreshButton.onClick.AddListener(OnRefreshClicked);

            if (hostDropdown != null)
                hostDropdown.onValueChanged.AddListener(OnDropdownValueChanged);

            if (discoveryClient != null)
                discoveryClient.OnHostDiscovered += OnHostDiscovered;
            else
            {
                // Try to find it if not assigned
                discoveryClient = FindObjectOfType<NetworkDiscoveryClient>();
                if (discoveryClient != null)
                {
                    discoveryClient.OnHostDiscovered += OnHostDiscovered;
                }
            }

            // Auto-start discovery when panel opens
            OnRefreshClicked();
        }

        private void OnDisable()
        {
            if (refreshButton != null)
                refreshButton.onClick.RemoveListener(OnRefreshClicked);

            if (hostDropdown != null)
                hostDropdown.onValueChanged.RemoveListener(OnDropdownValueChanged);

            if (discoveryClient != null)
            {
                discoveryClient.OnHostDiscovered -= OnHostDiscovered;
                discoveryClient.StopDiscovery();
            }
        }

        private void OnRefreshClicked()
        {
            if (discoveryClient == null) return;

            hostDropdown.ClearOptions();
            _currentHosts.Clear();
            
            hostDropdown.options.Add(new TMP_Dropdown.OptionData("Szukam hostów..."));
            hostDropdown.value = 0;
            hostDropdown.RefreshShownValue();

            discoveryClient.StartDiscovery();
        }

        private void OnHostDiscovered(NetworkDiscoveryMessage msg)
        {
            // Rebuild the list
            _currentHosts = discoveryClient.DiscoveredHosts.ToList();
            
            hostDropdown.ClearOptions();
            var options = new List<string>();
            foreach (var host in _currentHosts)
            {
                options.Add($"{host.HostName} ({host.HostIP})");
            }
            
            hostDropdown.AddOptions(options);

            // Zawsze uaktualnij wybrany IP do tego, co obecnie widnieje w dropdownie
            OnDropdownValueChanged(hostDropdown.value);
            hostDropdown.RefreshShownValue();
        }

        private void OnDropdownValueChanged(int index)
        {
            if (index >= 0 && index < _currentHosts.Count)
            {
                var selected = _currentHosts[index];
                if (onHostSelectedChannel != null)
                    onHostSelectedChannel.Raise(selected.HostIP);
            }
        }
    }
}
