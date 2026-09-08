// Runs only when the active build target is visionOS (or iOS) — UnityEditor.iOS.Xcode ships with
// the Apple platform modules and is not available on a Windows Editor without them.
#if UNITY_VISIONOS || UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace SuperAnretan.RemoteControl.Editor
{
    // Wires the generated Xcode project for the Vision Pro host: adds the LiveKitWebRTC Swift
    // package (WebRTC.xcframework with visionOS slices), links ReplayKit and writes the Info.plist
    // usage descriptions. Idempotent, so it is safe on "Append" builds.
    public static class RemoteControlVisionOSPostProcessor
    {
        // Pinned LiveKit WebRTC build (visionOS 2.2+ device + simulator slices).
        public const string WebRtcPackageUrl = "https://github.com/livekit/webrtc-xcframework";
        public const string WebRtcPackageVersion = "150.7871.01";
        public const string WebRtcProductName = "LiveKitWebRTC";

        [PostProcessBuild(100)]
        public static void OnPostProcessBuild(BuildTarget target, string buildPath)
        {
            if (target != BuildTarget.VisionOS) return;

            string projectPath = ResolvePbxProjectPath(buildPath);
            if (projectPath == null)
            {
                Debug.LogError($"[RemoteControl] No .xcodeproj found under {buildPath} — WebRTC not wired.");
                return;
            }

            var project = new PBXProject();
            project.ReadFromFile(projectPath);

            string frameworkTarget = project.GetUnityFrameworkTargetGuid();
            if (string.IsNullOrEmpty(frameworkTarget))
            {
                Debug.LogError("[RemoteControl] UnityFramework target not found — WebRTC not wired.");
                return;
            }
            string mainTarget = ResolveMainTargetGuid(project);

            // WebRTC via Swift Package Manager (adds the xcframework with xros slices).
            string packageGuid = project.AddRemotePackageReferenceAtVersion(WebRtcPackageUrl, WebRtcPackageVersion);
            project.AddRemotePackageFrameworkToProject(frameworkTarget, WebRtcProductName, packageGuid, false);
            // LiveKitWebRTC is a dynamic framework: the app target has to link it too, otherwise Xcode
            // never copies it into the .app bundle and the host crashes on launch with a dyld miss.
            if (!string.IsNullOrEmpty(mainTarget))
                project.AddRemotePackageFrameworkToProject(mainTarget, WebRtcProductName, packageGuid, false);

            // System frameworks used by the native plugin.
            project.AddFrameworkToProject(frameworkTarget, "ReplayKit.framework", false);
            project.AddFrameworkToProject(frameworkTarget, "CoreMedia.framework", false);
            project.AddFrameworkToProject(frameworkTarget, "CoreVideo.framework", false);

            project.AddBuildProperty(frameworkTarget, "CLANG_ENABLE_MODULES", "YES");
            project.WriteToFile(projectPath);

            // Info.plist
            string plistPath = Path.Combine(buildPath, "Info.plist");
            if (File.Exists(plistPath))
            {
                var plist = new PlistDocument();
                plist.ReadFromFile(plistPath);
                var root = plist.root;

                SetIfMissing(root, "NSLocalNetworkUsageDescription",
                    "Streams the app view to a remote controller on your local network.");
                SetIfMissing(root, "NSScreenCaptureUsageDescription",
                    "Streams the app view to a remote controller.");

                plist.WriteToFile(plistPath);
            }

            Debug.Log($"[RemoteControl] visionOS Xcode project configured: {WebRtcProductName} " +
                      $"{WebRtcPackageVersion}, ReplayKit (main target {mainTarget}).");
        }

        // PBXProject.GetPBXProjectPath() hardcodes Unity-iPhone.xcodeproj; a visionOS build emits
        // Unity-VisionOS.xcodeproj, so find the real one.
        private static string ResolvePbxProjectPath(string buildPath)
        {
            foreach (string projectName in new[] { "Unity-VisionOS.xcodeproj", "Unity-iPhone.xcodeproj" })
            {
                string candidate = Path.Combine(buildPath, projectName, "project.pbxproj");
                if (File.Exists(candidate)) return candidate;
            }

            foreach (string directory in Directory.GetDirectories(buildPath, "*.xcodeproj"))
            {
                string candidate = Path.Combine(directory, "project.pbxproj");
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        // GetUnityMainTargetGuid() also assumes "Unity-iPhone".
        private static string ResolveMainTargetGuid(PBXProject project)
        {
            foreach (string targetName in new[] { "Unity-VisionOS", "Unity-iPhone" })
            {
                string guid = project.TargetGuidByName(targetName);
                if (!string.IsNullOrEmpty(guid)) return guid;
            }

            return null;
        }

        private static void SetIfMissing(PlistElementDict dict, string key, string value)
        {
            if (!dict.values.ContainsKey(key)) dict.SetString(key, value);
        }
    }
}
#endif
