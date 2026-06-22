using System.Collections.Generic;
using UnityEngine;

namespace VRInteraction.AI
{
    public class AiPlanPreview : MonoBehaviour
    {
        private LineRenderer _line;
        private readonly List<GameObject> _targets = new List<GameObject>();

        public void Show(AiCommandResponse response)
        {
            Clear();
            if (response == null ||
                response.plan_ir == null ||
                response.plan_ir.waypoints == null ||
                response.plan_ir.waypoints.Length == 0)
                return;

            EnsureLine();
            _line.positionCount = response.plan_ir.waypoints.Length;
            for (int i = 0; i < response.plan_ir.waypoints.Length; i++)
                _line.SetPosition(i, AiModelUtil.ToVector3(
                    response.plan_ir.waypoints[i].position_m));

            for (int i = 0; i < response.plan_ir.waypoints.Length; i++)
            {
                Vector3 p = AiModelUtil.ToVector3(
                    response.plan_ir.waypoints[i].position_m);
                CreateTargetMarker(
                    p,
                    i,
                    i == response.plan_ir.waypoints.Length - 1);
            }
        }

        private void CreateTargetMarker(Vector3 position, int index, bool last)
        {
            var target = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            target.name = $"AI_TargetPreview_{index + 1}";
            target.transform.position = position;
            target.transform.localScale = Vector3.one * (last ? 0.08f : 0.055f);
            var col = target.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var r = target.GetComponent<Renderer>();
            if (r != null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Lit") ??
                         Shader.Find("Standard");
                var mat = new Material(sh);
                Color color = last
                    ? new Color(1f, 0.78f, 0.15f)
                    : new Color(0.25f, 0.85f, 1f);
                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", color);
                if (mat.HasProperty("_Color"))
                    mat.SetColor("_Color", color);
                r.sharedMaterial = mat;
            }
            _targets.Add(target);
        }

        public void Clear()
        {
            if (_line != null) _line.positionCount = 0;
            for (int i = 0; i < _targets.Count; i++)
                if (_targets[i] != null) Destroy(_targets[i]);
            _targets.Clear();
        }

        private void EnsureLine()
        {
            if (_line != null) return;
            var go = new GameObject("AI_PathPreview");
            go.transform.SetParent(transform, false);
            _line = go.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.widthMultiplier = 0.015f;
            _line.numCornerVertices = 4;
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ??
                     Shader.Find("Sprites/Default");
            _line.material = new Material(sh);
            _line.startColor = new Color(1f, 0.78f, 0.15f, 1f);
            _line.endColor = new Color(1f, 0.78f, 0.15f, 1f);
        }
    }
}
