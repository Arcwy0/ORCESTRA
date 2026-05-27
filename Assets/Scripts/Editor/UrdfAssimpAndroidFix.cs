using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VRInteraction.Editor
{
    /// <summary>
    /// Works around a packaging bug in <c>com.unity.robotics.urdf-importer</c>:
    /// its Windows AssimpNet native DLLs (win/x86 and win/x86_64 — both named
    /// <c>assimp.dll</c>) are marked compatible with "Any Platform", so an
    /// Android build tries to include both and fails with:
    ///   <i>"Cannot include plugin ... since plugin with the same name and
    ///   architecture was already added"</i>.
    ///
    /// These DLLs are only used for <b>runtime</b> mesh import on desktop, which
    /// this project never does (robots are pre-imported into prefabs, and the
    /// package ships no Android assimp library anyway). So we mark them
    /// incompatible with Android right before an Android build, leaving them
    /// intact for the Windows Editor/Standalone. The package is immutable
    /// (Package Cache), so we change the importer in-memory each build instead of
    /// editing its .meta; <see cref="PluginImporter.SaveAndReimport"/> is wrapped
    /// in try/catch because it throws on immutable package assets.
    /// </summary>
    public class UrdfAssimpAndroidFix : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android)
                return;

            foreach (var imp in PluginImporter.GetImporters(BuildTarget.Android))
            {
                if (imp == null) continue;

                var path = imp.assetPath.Replace('\\', '/');
                bool isWinAssimp =
                    path.Contains("AssimpNet/Native/win") &&
                    path.EndsWith("assimp.dll");
                if (!isWinAssimp) continue;

                imp.SetCompatibleWithAnyPlatform(false);
                imp.SetCompatibleWithPlatform(BuildTarget.Android, false);
                try { imp.SaveAndReimport(); }
                catch { /* immutable package — the in-memory change is enough */ }

                Debug.Log($"[UrdfAssimpAndroidFix] Excluded '{path}' from the Android build.");
            }
        }
    }
}
