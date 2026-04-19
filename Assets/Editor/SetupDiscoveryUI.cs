using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using TMPro;
using SuperAnretan.RemoteControl;

public static class SetupDiscoveryUI
{
    [MenuItem("Tools/Remote Control/Create Discovery UI Prefab")]
    public static void CreatePrefab()
    {
        // Check if folder exists
        if (!AssetDatabase.IsValidFolder("Assets/RemoteControlCore/Runtime/UI"))
        {
            AssetDatabase.CreateFolder("Assets/RemoteControlCore/Runtime", "UI");
        }
        if (!AssetDatabase.IsValidFolder("Assets/RemoteControlCore/Runtime/UI/Prefabs"))
        {
            AssetDatabase.CreateFolder("Assets/RemoteControlCore/Runtime/UI", "Prefabs");
        }

        // 1. Panel
        GameObject panel = new GameObject("NetworkDiscoveryPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(VerticalLayoutGroup));
        panel.GetComponent<Image>().color = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        var layout = panel.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 10, 10);
        layout.spacing = 10;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        panel.GetComponent<RectTransform>().sizeDelta = new Vector2(350, 100);

        // 2. Dropdown
        // Using TMP default controls
        GameObject dropdown = DefaultControls.CreateDropdown(new DefaultControls.Resources());
        dropdown.name = "HostDropdown";
        dropdown.transform.SetParent(panel.transform, false);
        
        // Convert to TMP_Dropdown: Remove standard Dropdown and Texts
        Object.DestroyImmediate(dropdown.GetComponent<Dropdown>());
        var tmpDropdown = dropdown.AddComponent<TMP_Dropdown>();
        
        // We actually just use the TMPro menu item if we want a perfect one, 
        // but let's just make it a raw TMP_Dropdown with a basic text for now to avoid manual assembly of templates.
        var labelObj = dropdown.transform.Find("Label");
        Object.DestroyImmediate(labelObj.GetComponent<Text>());
        var tmpLabel = labelObj.gameObject.AddComponent<TextMeshProUGUI>();
        tmpLabel.color = Color.black;
        tmpDropdown.captionText = tmpLabel;

        // 3. Refresh Button
        GameObject btnObj = DefaultControls.CreateButton(new DefaultControls.Resources());
        btnObj.name = "RefreshButton";
        btnObj.transform.SetParent(panel.transform, false);
        var oldText = btnObj.GetComponentInChildren<Text>();
        GameObject textObj = oldText.gameObject;
        Object.DestroyImmediate(oldText);
        var btnText = textObj.AddComponent<TextMeshProUGUI>();
        btnText.text = "Refresh";
        btnText.color = Color.black;
        btnText.alignment = TextAlignmentOptions.Center;

        // 4. Client & UI Scripts
        var discoveryClient = panel.AddComponent<NetworkDiscoveryClient>();
        var discoveryUI = panel.AddComponent<DiscoveryUI>();
        
        // Link up
        var serializedObject = new SerializedObject(discoveryUI);
        serializedObject.FindProperty("discoveryClient").objectReferenceValue = discoveryClient;
        serializedObject.FindProperty("hostDropdown").objectReferenceValue = tmpDropdown;
        serializedObject.FindProperty("refreshButton").objectReferenceValue = btnObj.GetComponent<Button>();
        // Network Config
        var config = AssetDatabase.LoadAssetAtPath<NetworkConfig>("Assets/RemoteControlCore/Runtime/DefaultSetup/SO/NetworkConfig.asset");
        var clientSerializedObject = new SerializedObject(discoveryClient);
        clientSerializedObject.FindProperty("networkConfig").objectReferenceValue = config;
        clientSerializedObject.ApplyModifiedProperties();
        serializedObject.ApplyModifiedProperties();

        // Save Prefab
        PrefabUtility.SaveAsPrefabAsset(panel, "Assets/RemoteControlCore/Runtime/UI/Prefabs/NetworkDiscoveryPanel.prefab");
        Object.DestroyImmediate(panel);
        Debug.Log("Prefab created successfully.");
    }
}
