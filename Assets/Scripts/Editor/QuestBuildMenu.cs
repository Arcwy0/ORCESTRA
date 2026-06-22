using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VRInteraction.Editor
{
    public static class QuestBuildMenu
    {
        private const string OutputPath = "Builds/ORCESTRA_Robot_AI_Quest.apk";

        [MenuItem("UR3/Build/Build Quest APK", priority = 200)]
        public static void BuildQuestApk()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                Debug.LogError("[Quest Build] Switch the active platform to Android first.");
                return;
            }

            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[Quest Build] No enabled scenes in Build Settings.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = OutputPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[Quest Build] Built {OutputPath} " +
                          $"({summary.totalSize / (1024f * 1024f):0.0} MB).");
            }
            else
            {
                Debug.LogError($"[Quest Build] Build failed: {summary.result}.");
            }
        }

        [MenuItem("UR3/Build/Build Quest APK", true)]
        private static bool BuildQuestApkValidate()
        {
            return !EditorApplication.isCompiling;
        }
    }
}
