using UnityEngine;
using UnityEngine.Rendering;

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
            AddVisibilityMarker(ghost);
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
                if (IsVisibilityMarker(r.transform)) continue;
                var m = r.sharedMaterial;
                if (m == null) continue;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
                if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            }
        }

        public static bool IsVisibilityMarker(Transform t)
        {
            while (t != null)
            {
                if (t.name == "PlacementVisibilityMarker")
                    return true;
                t = t.parent;
            }
            return false;
        }

        public static Material TransparentMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
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
            if (m.HasProperty("_Cull"))
                m.SetFloat("_Cull", (float)CullMode.Off);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            m.DisableKeyword("_SURFACE_TYPE_OPAQUE");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;

            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            return m;
        }

        private static void AddVisibilityMarker(GameObject ghost)
        {
            if (!TryRendererBounds(ghost, out var bounds))
                return;

            var marker = new GameObject("PlacementVisibilityMarker");
            marker.transform.SetParent(ghost.transform, false);

            Vector3 worldBase = new Vector3(
                bounds.center.x,
                bounds.min.y + 0.025f,
                bounds.center.z);
            marker.transform.localPosition =
                ghost.transform.InverseTransformPoint(worldBase);

            float radius = Mathf.Clamp(
                Mathf.Max(bounds.extents.x, bounds.extents.z) + 0.08f,
                0.18f,
                0.70f);
            float height = Mathf.Clamp(bounds.size.y + 0.20f, 0.35f, 1.20f);
            var mat = MarkerMaterial();

            const int segments = 64;
            var ring = marker.AddComponent<LineRenderer>();
            ring.useWorldSpace = false;
            ring.loop = true;
            ring.positionCount = segments;
            ring.widthMultiplier = 0.018f;
            ring.sharedMaterial = mat;
            ring.shadowCastingMode = ShadowCastingMode.Off;
            for (int i = 0; i < segments; i++)
            {
                float a = (Mathf.PI * 2f * i) / segments;
                ring.SetPosition(i, new Vector3(
                    Mathf.Cos(a) * radius,
                    0f,
                    Mathf.Sin(a) * radius));
            }

            var poleGo = new GameObject("HeightMarker");
            poleGo.transform.SetParent(marker.transform, false);
            var pole = poleGo.AddComponent<LineRenderer>();
            pole.useWorldSpace = false;
            pole.positionCount = 2;
            pole.widthMultiplier = 0.016f;
            pole.sharedMaterial = mat;
            pole.shadowCastingMode = ShadowCastingMode.Off;
            pole.SetPosition(0, Vector3.zero);
            pole.SetPosition(1, Vector3.up * height);
        }

        private static bool TryRendererBounds(GameObject ghost, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var r in ghost.GetComponentsInChildren<Renderer>(true))
            {
                if (!any)
                {
                    bounds = r.bounds;
                    any = true;
                }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        private static Material MarkerMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                      ?? Shader.Find("Sprites/Default")
                      ?? Shader.Find("Standard");
            var m = new Material(shader);
            var color = new Color(1f, 0.95f, 0.12f, 1f);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_Cull"))
                m.SetFloat("_Cull", (float)CullMode.Off);
            return m;
        }
    }
}
