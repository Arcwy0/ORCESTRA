using UnityEditor;
using UnityEngine;
using VRInteraction.AI;

namespace VRInteraction.Editor
{
    public static class AiSceneBootstrapper
    {
        [MenuItem("UR3/Setup AI Robot Control")]
        public static void SetupAiRobotControl()
        {
            var existing = Object.FindAnyObjectByType<RobotAiController>(
                FindObjectsInactive.Include);
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                return;
            }

            var go = new GameObject("AI_RobotControl");
            go.AddComponent<RobotAiController>();
            Selection.activeGameObject = go;
            EditorSceneManagerProxy.MarkDirty();
        }

        private static class EditorSceneManagerProxy
        {
            public static void MarkDirty()
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            }
        }
    }
}
