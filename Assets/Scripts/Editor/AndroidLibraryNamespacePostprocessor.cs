using System;
using System.IO;
using UnityEditor.Android;
using UnityEngine;

namespace VRInteraction.Editor
{
    /// <summary>
    /// Unity 6 uses a recent Android Gradle Plugin, which requires every
    /// Android library module to declare an explicit namespace. The local
    /// permissions-only .androidlib has no Java sources, but AGP still rejects
    /// the generated module if the namespace is missing.
    /// </summary>
    public sealed class AndroidLibraryNamespacePostprocessor : IPostGenerateGradleAndroidProject
    {
        private const string PermissionsModule = "ORCESTRAAndroidPermissions.androidlib";
        private const string PermissionsNamespace = "com.orcestra.robotai.permissions";

        public int callbackOrder => -900;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string buildGradlePath = Path.Combine(path, PermissionsModule, "build.gradle");
            if (!File.Exists(buildGradlePath))
                return;

            string gradle = File.ReadAllText(buildGradlePath);
            if (gradle.IndexOf("namespace ", StringComparison.Ordinal) >= 0)
                return;

            const string androidBlock = "android {";
            int androidIndex = gradle.IndexOf(androidBlock, StringComparison.Ordinal);
            if (androidIndex < 0)
            {
                Debug.LogWarning($"[AndroidLibraryNamespacePostprocessor] Could not find android block in {buildGradlePath}.");
                return;
            }

            int insertIndex = androidIndex + androidBlock.Length;
            string patched = gradle.Insert(insertIndex, Environment.NewLine + $"    namespace '{PermissionsNamespace}'");
            File.WriteAllText(buildGradlePath, patched);
            Debug.Log($"[AndroidLibraryNamespacePostprocessor] Added namespace to {PermissionsModule}.");
        }
    }
}
