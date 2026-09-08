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
    /// <summary>
    /// Wires the generated Xcode project for the Vision Pro host:
    ///   • adds the LiveKitWebRTC Swift package (WebRTC.xcframework with visionOS slices) to UnityFramework
    ///   • links ReplayKit (and ScreenCaptureKit when enabled)
    ///   • adds the Info.plist usage descriptions the OS asks for
    /// Idempotent — safe to run on "Append" builds.
    /// </summary>
    public static class RemoteControlVisionOSPostProcessor
    {
        /// <summary>Pinned LiveKit WebRTC build (visionOS 2.2+ device + simulator slices).</summary>
        public const string WebRtcPackageUrl = "https://github.com/livekit/webrtc-xcframework";
        public const string WebRtcPackageVersion = "150.7871.01";
        public const string WebRtcProductName = "LiveKitWebRTC";

        /// <summary>
        /// Compile the ScreenCaptureKit backend. Requires the Xcode 27 SDK (visionOS 27 beta).
        /// Off by default so builds with Xcode 16/26 keep working (ReplayKit path).
        /// </summary>
        public const bool EnableScreenCaptureKit = false;

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

            if (EnableScreenCaptureKit)
            {
                project.AddFrameworkToProject(frameworkTarget, "ScreenCaptureKit.framework", true);
                project.AddBuildProperty(frameworkTarget, "GCC_PREPROCESSOR_DEFINITIONS", "VPR_ENABLE_SCREENCAPTUREKIT=1");
            }

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

            Debug.Log($"[RemoteControl] visionOS Xcode project configured: {WebRtcProductName} {WebRtcPackageVersion}, ReplayKit" +
                      (EnableScreenCaptureKit ? ", ScreenCaptureKit" : "") + $" (main target {mainTarget}).");
        }

        /// <summary>
        /// PBXProject.GetPBXProjectPath() hardcodes the iOS project name (Unity-iPhone.xcodeproj);
        /// a visionOS build emits Unity-VisionOS.xcodeproj, so resolve the real one.
        /// </summary>
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

        /// <summary>GetUnityMainTargetGuid() also assumes "Unity-iPhone"; the visionOS app target is named differently.</summary>
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
