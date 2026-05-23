using UnityEngine;
using UnityEngine.Rendering;
using VRInteraction.Robot;

namespace VRInteraction.Placement
{
    /// <summary>
    /// Turns a robot prefab into a translucent, physics-free "ghost" used to
    /// preview where the base will stand before the operator confirms.
    /// </summary>
    public static class GhostBuilder
    {
        public static GameObject CreateGhost(GameObject prefab, Color tint)
        {
            var ghost = Object.Instantiate(prefab);
            ghost.name = prefab.name + " (Ghost)";

            // Kill all control & physics so it is purely visual.
            foreach (var mb in ghost.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null) Object.DestroyImmediate(mb);
            foreach (var col in ghost.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(col);
            foreach (var rb in ghost.GetComponentsInChildren<Rigidbody>(true))
                Object.DestroyImmediate(rb);
            // Destroy ArticulationBodies leaf-first so the chain stays valid.
            var abs = ghost.GetComponentsInChildren<ArticulationBody>(true);
            for (int i = abs.Length - 1; i >= 0; i--)
                Object.DestroyImmediate(abs[i]);

            var mat = TransparentMaterial(tint);
            foreach (var r in ghost.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
                r.shadowCastingMode = ShadowCastingMode.Off;
            }
            return ghost;
        }

        /// <summary>
        /// Recolours an existing ghost (used for the overlap warning: cyan
        /// when clear, red when the footprint intersects another robot).
        /// All ghost renderers share one material, so one set call repaints
        /// the whole preview.
        /// </summary>
        public static void SetTint(GameObject ghost, Color color)
        {
            if (ghost == null) return;
            foreach (var r in ghost.GetComponentsInChildren<Renderer>(true))
            {
                var m = r.sharedMaterial;
                if (m == null) continue;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
                if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            }
        }

        public static Material TransparentMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var m = new Material(shader);

            // URP transparent surface configuration.
            m.SetOverrideTag("RenderType", "Transparent");
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
            if (m.HasProperty("_SrcBlend"))
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend"))
                m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            m.DisableKeyword("_SURFACE_TYPE_OPAQUE");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;

            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            return m;
        }
    }
}
