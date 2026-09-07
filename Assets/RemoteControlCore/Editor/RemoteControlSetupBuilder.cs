using System;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SuperAnretan.RemoteControl.Editor
{
    /// <summary>
    /// Reproducible generator for the WebGL ↔ Vision Pro setup:
    ///   Tools ▸ Remote Control ▸ WebRTC ▸ Create Prefabs
    ///   Tools ▸ Remote Control ▸ WebRTC ▸ Build WebGL Controller Scene
    ///   Tools ▸ Remote Control ▸ WebRTC ▸ Build Vision Pro Host Scene
    ///   Tools ▸ Remote Control ▸ WebRTC ▸ Create Everything
    /// Existing native prefabs/scenes are left untouched; the old ControllerScene is preserved as
    /// NativeControllerScene before being rebuilt for WebGL.
    /// </summary>
    public static class RemoteControlSetupBuilder
    {
        private const string SoDir = "Assets/RemoteControlCore/Runtime/DefaultSetup/SO/";
        private const string PrefabDir = "Assets/RemoteControlCore/Runtime/DefaultSetup/Prefabs/";
        private const string ScenesDir = "Assets/Scenes/";

        private const string WebGLClientPrefabPath = PrefabDir + "RemoteControl_WebGLClientCore.prefab";
        private const string VisionProHostPrefabPath = PrefabDir + "RemoteControl_VisionProHost.prefab";
        private const string DiscoveryPanelPrefabPath = PrefabDir + "NetworkDiscoveryPanel.prefab";

        private const string ControllerScenePath = ScenesDir + "ControllerScene.unity";
        private const string NativeControllerScenePath = ScenesDir + "NativeControllerScene.unity";
        private const string VisionProHostScenePath = ScenesDir + "VisionProHostScene.unity";

        // ───────── menu ─────────

        [MenuItem("Tools/Remote Control/WebRTC/Create Everything", priority = 0)]
        public static void CreateEverything()
        {
            CreatePrefabs();
            BuildWebGLControllerScene();
            BuildVisionProHostScene();
            Debug.Log("[RemoteControl] WebGL ↔ Vision Pro setup complete.");
        }

        [MenuItem("Tools/Remote Control/WebRTC/Create Prefabs", priority = 10)]
        public static void CreatePrefabs()
        {
            EnsureNetworkConfigSaved();
            CreateWebGLClientPrefab();
            CreateDiscoveryPanelPrefab();
            CreateVisionProHostPrefab();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        [MenuItem("Tools/Remote Control/WebRTC/Build WebGL Controller Scene", priority = 11)]
        public static void BuildWebGLControllerScene()
        {
            if (!File.Exists(WebGLClientPrefabPath) || !File.Exists(DiscoveryPanelPrefabPath)) CreatePrefabs();

            // Keep the original native (IP input) controller scene around.
            if (File.Exists(ControllerScenePath) && !File.Exists(NativeControllerScenePath))
            {
                AssetDatabase.CopyAsset(ControllerScenePath, NativeControllerScenePath);
                Debug.Log($"[RemoteControl] Preserved native controller scene as {NativeControllerScenePath}");
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cam = CreateCamera(new Color(0.09f, 0.10f, 0.12f));
            cam.orthographic = true;

            CreateEventSystem();

            var canvas = CreateCanvas("Canvas");
            var panelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DiscoveryPanelPrefabPath);
            var panel = (GameObject)PrefabUtility.InstantiatePrefab(panelPrefab, scene);
            panel.transform.SetParent(canvas.transform, false);
            Stretch(panel.GetComponent<RectTransform>());

            var corePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(WebGLClientPrefabPath);
            PrefabUtility.InstantiatePrefab(corePrefab, scene);

            EditorSceneManager.SaveScene(scene, ControllerScenePath);
            Debug.Log($"[RemoteControl] WebGL controller scene saved: {ControllerScenePath}");
        }

        [MenuItem("Tools/Remote Control/WebRTC/Build Vision Pro Host Scene", priority = 12)]
        public static void BuildVisionProHostScene()
        {
            if (!File.Exists(VisionProHostPrefabPath)) CreatePrefabs();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cam = CreateCamera(new Color(0.12f, 0.14f, 0.18f));
            cam.transform.position = new Vector3(0, 1, -4);

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);

            // Example target + handler — the same "demo_cube" / "set_color" the native demo uses.
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "DemoCube";
            cube.transform.position = new Vector3(0, 1, 0);
            cube.transform.rotation = Quaternion.Euler(0, 35, 0);
            var target = cube.AddComponent<CommandTarget>();
            SetField(target, "_targetId", "demo_cube");
            SetField(target, "_registry", LoadSo("TargetRegistry"));

            var handlerType = FindType("SuperAnretan.RemoteControl.Samples.SetColorHandler");
            if (handlerType != null)
            {
                var handler = cube.AddComponent(handlerType);
                SetField(handler, "_handlerRegistry", LoadSo("HandlerRegistry"));
            }
            else Debug.LogWarning("[RemoteControl] SetColorHandler not found — add a handler to DemoCube manually.");

            var hostPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(VisionProHostPrefabPath);
            PrefabUtility.InstantiatePrefab(hostPrefab, scene);

            // On-device log overlay (works in visionOS windowed mode and in the Editor).
            CreateEventSystem();
            var canvas = CreateCanvas("HostCanvas");
            var log = CreateLogPanel(canvas.transform, new Vector2(0.02f, 0.02f), new Vector2(0.98f, 0.35f));
            var hostLog = canvas.gameObject.AddComponent<DebugLogUI>();
            SetField(hostLog, "_logChannel", LoadSo("LogChannel"));
            SetField(hostLog, "_logText", log);
            SetField(hostLog, "_maxLines", 16);

            var bootstrapType = FindType("SuperAnretan.RemoteControl.Samples.DemoHostBootstrap");
            if (bootstrapType != null)
            {
                var bootstrap = new GameObject("HostBootstrap").AddComponent(bootstrapType);
                SetField(bootstrap, "_logChannel", LoadSo("LogChannel"));
            }

            EditorSceneManager.SaveScene(scene, VisionProHostScenePath);
            Debug.Log($"[RemoteControl] Vision Pro host scene saved: {VisionProHostScenePath}");
        }

        // ───────── prefabs ─────────

        private static void CreateWebGLClientPrefab()
        {
            var root = new GameObject("RemoteControl_WebGLClientCore");
            try
            {
                var discovery = root.AddComponent<WebGLDiscoveryClient>();
                SetField(discovery, "_networkConfig", LoadSo("NetworkConfig"));
                SetField(discovery, "_logChannel", LoadSo("LogChannel"));

                var transport = root.AddComponent<WebGLRemoteTransport>();
                SetField(transport, "_networkConfig", LoadSo("NetworkConfig"));
                SetField(transport, "_commandSendChannel", LoadSo("CommandSendChannel"));
                SetField(transport, "_connectRequestChannel", LoadSo("ConnectRequestChannel"));
                SetField(transport, "_disconnectRequestChannel", LoadSo("DisconnectRequestChannel"));
                SetField(transport, "_onConnectedChannel", LoadSo("OnConnectedChannel"));
                SetField(transport, "_onDisconnectedChannel", LoadSo("OnDisconnectedChannel"));
                SetField(transport, "_logChannel", LoadSo("LogChannel"));

                SavePrefab(root, WebGLClientPrefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void CreateVisionProHostPrefab()
        {
            var root = new GameObject("RemoteControl_VisionProHost");
            try
            {
                var signaling = root.AddComponent<VisionProSignalingClient>();
                SetField(signaling, "_networkConfig", LoadSo("NetworkConfig"));
                SetField(signaling, "_logChannel", LoadSo("LogChannel"));

                var host = root.AddComponent<VisionProWebRtcHost>();
                SetField(host, "_networkConfig", LoadSo("NetworkConfig"));
                SetField(host, "_signaling", signaling);
                SetField(host, "_commandReceivedChannel", LoadSo("CommandReceivedChannel"));
                SetField(host, "_onClientConnectedChannel", LoadSo("OnClientConnectedChannel"));
                SetField(host, "_onClientDisconnectedChannel", LoadSo("OnClientDisconnectedChannel"));
                SetField(host, "_logChannel", LoadSo("LogChannel"));

                var processor = root.AddComponent<CommandProcessor>();
                SetField(processor, "_commandReceivedChannel", LoadSo("CommandReceivedChannel"));
                SetField(processor, "_handlerRegistry", LoadSo("HandlerRegistry"));
                SetField(processor, "_targetRegistry", LoadSo("TargetRegistry"));
                SetField(processor, "_logChannel", LoadSo("LogChannel"));

                SavePrefab(root, VisionProHostPrefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void CreateDiscoveryPanelPrefab()
        {
            var res = UiResources();
            var root = new GameObject("NetworkDiscoveryPanel", typeof(RectTransform));
            try
            {
                Stretch(root.GetComponent<RectTransform>());

                // ── status ──
                var status = CreateTmpText(root.transform, "StatusText", "Searching for devices...", 26, TextAlignmentOptions.Left);
                Anchor(status, new Vector2(0.03f, 0.90f), new Vector2(0.62f, 0.97f));

                // ── picker row ──
                var pickerRow = new GameObject("PickerRow", typeof(RectTransform));
                pickerRow.transform.SetParent(root.transform, false);
                Anchor(pickerRow.GetComponent<RectTransform>(), new Vector2(0.03f, 0.80f), new Vector2(0.97f, 0.88f));

                var dropdownGo = TMP_DefaultControls.CreateDropdown(res);
                dropdownGo.name = "DeviceDropdown";
                dropdownGo.transform.SetParent(pickerRow.transform, false);
                Anchor(dropdownGo.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0.55f, 1f));
                var dropdown = dropdownGo.GetComponent<TMP_Dropdown>();
                dropdown.ClearOptions();
                SetTmpFontSize(dropdownGo, 22);

                var refreshBtn = CreateButton(pickerRow.transform, "RefreshButton", "Refresh", res, new Vector2(0.57f, 0f), new Vector2(0.72f, 1f));
                var connectBtn = CreateButton(pickerRow.transform, "ConnectButton", "Connect", res, new Vector2(0.74f, 0f), new Vector2(0.86f, 1f));
                var disconnectBtn = CreateButton(pickerRow.transform, "DisconnectButton", "Disconnect", res, new Vector2(0.87f, 0f), new Vector2(1f, 1f));
                ColorButton(connectBtn, new Color(0.35f, 0.75f, 0.40f));
                ColorButton(disconnectBtn, new Color(0.80f, 0.35f, 0.35f));

                // ── connected group (hidden until connected) ──
                var connected = new GameObject("ConnectedGroup", typeof(RectTransform));
                connected.transform.SetParent(root.transform, false);
                Anchor(connected.GetComponent<RectTransform>(), new Vector2(0.03f, 0.30f), new Vector2(0.97f, 0.78f));

                var videoFrame = new GameObject("VideoFrame", typeof(RectTransform), typeof(Image));
                videoFrame.transform.SetParent(connected.transform, false);
                Anchor(videoFrame.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0.66f, 1f));
                videoFrame.GetComponent<Image>().color = new Color(0, 0, 0, 0.6f);

                var videoGo = new GameObject("VideoView", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
                videoGo.transform.SetParent(videoFrame.transform, false);
                var videoRt = videoGo.GetComponent<RectTransform>();
                videoRt.anchorMin = new Vector2(0.5f, 0.5f);
                videoRt.anchorMax = new Vector2(0.5f, 0.5f);
                videoRt.sizeDelta = new Vector2(640, 360);
                var fitter = videoGo.GetComponent<AspectRatioFitter>();
                fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fitter.aspectRatio = 16f / 9f;
                var rawImage = videoGo.GetComponent<RawImage>();
                rawImage.color = Color.white;

                var videoView = videoGo.AddComponent<RemoteVideoView>();
                SetField(videoView, "_target", rawImage);
                SetField(videoView, "_aspectFitter", fitter);
                SetField(videoView, "_onDisconnectedChannel", LoadSo("OnDisconnectedChannel"));
                SetField(videoView, "_logChannel", LoadSo("LogChannel"));

                var controls = new GameObject("ControlButtons", typeof(RectTransform));
                controls.transform.SetParent(connected.transform, false);
                Anchor(controls.GetComponent<RectTransform>(), new Vector2(0.69f, 0f), new Vector2(1f, 1f));

                var title = CreateTmpText(controls.transform, "Title", "demo_cube → set_color", 22, TextAlignmentOptions.Center);
                Anchor(title, new Vector2(0f, 0.86f), new Vector2(1f, 1f));
                var redBtn = CreateButton(controls.transform, "RedBtn", "Red", res, new Vector2(0f, 0.60f), new Vector2(1f, 0.82f));
                var greenBtn = CreateButton(controls.transform, "GreenBtn", "Green", res, new Vector2(0f, 0.34f), new Vector2(1f, 0.56f));
                var blueBtn = CreateButton(controls.transform, "BlueBtn", "Blue", res, new Vector2(0f, 0.08f), new Vector2(1f, 0.30f));
                ColorButton(redBtn, new Color(0.85f, 0.30f, 0.30f));
                ColorButton(greenBtn, new Color(0.30f, 0.75f, 0.35f));
                ColorButton(blueBtn, new Color(0.30f, 0.45f, 0.90f));

                connected.SetActive(false);

                // ── log ──
                var logText = CreateLogPanel(root.transform, new Vector2(0.03f, 0.02f), new Vector2(0.97f, 0.27f));

                // ── behaviours on the root (always active) ──
                var panel = root.AddComponent<NetworkDiscoveryPanel>();
                SetField(panel, "_connectRequestChannel", LoadSo("ConnectRequestChannel"));
                SetField(panel, "_disconnectRequestChannel", LoadSo("DisconnectRequestChannel"));
                SetField(panel, "_onConnectedChannel", LoadSo("OnConnectedChannel"));
                SetField(panel, "_onDisconnectedChannel", LoadSo("OnDisconnectedChannel"));
                SetField(panel, "_logChannel", LoadSo("LogChannel"));
                SetField(panel, "_deviceDropdown", dropdown);
                SetField(panel, "_refreshButton", refreshBtn.GetComponent<Button>());
                SetField(panel, "_connectButton", connectBtn.GetComponent<Button>());
                SetField(panel, "_disconnectButton", disconnectBtn.GetComponent<Button>());
                SetField(panel, "_statusText", status.GetComponent<TextMeshProUGUI>());
                SetArray(panel, "_showWhenConnected", connected);
                // _discovery is resolved at runtime by DiscoveryBinder (prefab cannot reference a scene object).
                root.AddComponent<DiscoveryBinder>();

                var uiType = FindType("SuperAnretan.RemoteControl.Samples.DemoControllerUI");
                if (uiType != null)
                {
                    var ui = root.AddComponent(uiType);
                    SetField(ui, "_commandSendChannel", LoadSo("CommandSendChannel"));
                    SetField(ui, "_onConnectedChannel", LoadSo("OnConnectedChannel"));
                    SetField(ui, "_onDisconnectedChannel", LoadSo("OnDisconnectedChannel"));
                    SetField(ui, "_logChannel", LoadSo("LogChannel"));
                    SetField(ui, "_redButton", redBtn.GetComponent<Button>());
                    SetField(ui, "_greenButton", greenBtn.GetComponent<Button>());
                    SetField(ui, "_blueButton", blueBtn.GetComponent<Button>());
                    SetField(ui, "_targetId", "demo_cube");
                }

                var debugLog = root.AddComponent<DebugLogUI>();
                SetField(debugLog, "_logChannel", LoadSo("LogChannel"));
                SetField(debugLog, "_logText", logText);
                SetField(debugLog, "_maxLines", 14);

                SavePrefab(root, DiscoveryPanelPrefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        // ───────── scene helpers ─────────

        private static Camera CreateCamera(Color background)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            go.AddComponent<AudioListener>();
            go.transform.position = new Vector3(0, 1, -10);
            return cam;
        }

        private static void CreateEventSystem()
        {
            var es = new GameObject("EventSystem", typeof(EventSystem));
            var inputSystemModule = FindType("UnityEngine.InputSystem.UI.InputSystemUIInputModule", "Unity.InputSystem");
            if (inputSystemModule != null) es.AddComponent(inputSystemModule);
            else es.AddComponent<StandaloneInputModule>();
        }

        private static Canvas CreateCanvas(string name)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        private static TextMeshProUGUI CreateLogPanel(Transform parent, Vector2 anchorMin, Vector2 anchorMax)
        {
            var bg = new GameObject("LogPanel", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(parent, false);
            Anchor(bg.GetComponent<RectTransform>(), anchorMin, anchorMax);
            bg.GetComponent<Image>().color = new Color(0, 0, 0, 0.45f);

            var text = CreateTmpText(bg.transform, "LogText", "", 16, TextAlignmentOptions.BottomLeft);
            Anchor(text, new Vector2(0.01f, 0.03f), new Vector2(0.99f, 0.97f));
            var tmp = text.GetComponent<TextMeshProUGUI>();
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Truncate;
            tmp.color = new Color(0.85f, 0.95f, 0.85f);
            return tmp;
        }

        // ───────── UI helpers ─────────

        private static TMP_DefaultControls.Resources UiResources()
        {
            return new TMP_DefaultControls.Resources
            {
                standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
                background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
                inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd"),
                knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
                checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
                dropdown = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd"),
                mask = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd"),
            };
        }

        private static GameObject CreateButton(Transform parent, string name, string label, TMP_DefaultControls.Resources res,
            Vector2 anchorMin, Vector2 anchorMax)
        {
            var go = TMP_DefaultControls.CreateButton(res);
            go.name = name;
            go.transform.SetParent(parent, false);
            Anchor(go.GetComponent<RectTransform>(), anchorMin, anchorMax);
            var text = go.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) { text.text = label; text.fontSize = 22; text.fontStyle = FontStyles.Bold; }
            return go;
        }

        private static void ColorButton(GameObject button, Color color)
        {
            var image = button.GetComponent<Image>();
            if (image != null) image.color = color;
            var text = button.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) text.color = Color.white;
        }

        private static RectTransform CreateTmpText(Transform parent, string name, string text, float size, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.alignment = alignment;
            tmp.color = Color.white;
            return go.GetComponent<RectTransform>();
        }

        private static void SetTmpFontSize(GameObject root, float size)
        {
            foreach (var t in root.GetComponentsInChildren<TextMeshProUGUI>(true)) t.fontSize = size;
        }

        private static void Anchor(RectTransform rt, Vector2 min, Vector2 max)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void Stretch(RectTransform rt) => Anchor(rt, Vector2.zero, Vector2.one);

        // ───────── asset helpers ─────────

        private static ScriptableObject LoadSo(string name)
        {
            var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(SoDir + name + ".asset");
            if (so == null) Debug.LogError($"[RemoteControl] Missing SO asset: {SoDir}{name}.asset");
            return so;
        }

        private static void EnsureNetworkConfigSaved()
        {
            // Re-serialize so the new signaling/video fields get their defaults written to the asset.
            var config = LoadSo("NetworkConfig");
            if (config != null) EditorUtility.SetDirty(config);
        }

        private static void SavePrefab(GameObject root, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
            if (success) Debug.Log($"[RemoteControl] Prefab saved: {path}");
            else Debug.LogError($"[RemoteControl] Failed to save prefab: {path}");
        }

        private static void SetField(UnityEngine.Object target, string field, UnityEngine.Object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null) { Debug.LogWarning($"[RemoteControl] {target.GetType().Name} has no field '{field}'"); return; }
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetField(UnityEngine.Object target, string field, string value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null) { Debug.LogWarning($"[RemoteControl] {target.GetType().Name} has no field '{field}'"); return; }
            prop.stringValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetField(UnityEngine.Object target, string field, int value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null) return;
            prop.intValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetArray(UnityEngine.Object target, string field, params UnityEngine.Object[] values)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null) return;
            prop.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) prop.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Type FindType(string fullName, string assemblyHint = "Assembly-CSharp")
        {
            var type = Type.GetType($"{fullName}, {assemblyHint}");
            if (type != null) return type;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }
    }
}
