using UnityEngine;

namespace VRInteraction.AI
{
    public class AiPlanPreview : MonoBehaviour
    {
        private LineRenderer _line;
        private GameObject _target;

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

            Vector3 last = AiModelUtil.ToVector3(
                response.plan_ir.waypoints[
                    response.plan_ir.waypoints.Length - 1].position_m);
            _target = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _target.name = "AI_TargetPreview";
            _target.transform.position = last;
            _target.transform.localScale = Vector3.one * 0.08f;
            var col = _target.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var r = _target.GetComponent<Renderer>();
            if (r != null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Lit") ??
                         Shader.Find("Standard");
                var mat = new Material(sh);
                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", new Color(1f, 0.78f, 0.15f));
                if (mat.HasProperty("_Color"))
                    mat.SetColor("_Color", new Color(1f, 0.78f, 0.15f));
                r.sharedMaterial = mat;
            }
        }

        public void Clear()
        {
            if (_line != null) _line.positionCount = 0;
            if (_target != null) Destroy(_target);
            _target = null;
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
