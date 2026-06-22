using System.Collections.Generic;
using UnityEngine;

namespace VRInteraction.AI
{
    public static class AiManipulatorPathPlanner
    {
        private const float SafeRadiusScale = 1.45f;
        private const float ExtraSafeRadiusMeters = 0.08f;
        private const float MaxArcStepDegrees = 24f;

        public static List<Vector3> RepairBaseCrossingPath(
            IReadOnlyList<Vector3> waypoints,
            Vector3 basePosition,
            float baseAvoidRadiusMeters,
            float detourHeightMeters)
        {
            var repaired = new List<Vector3>();
            if (waypoints == null || waypoints.Count == 0)
                return repaired;

            float avoidRadius = Mathf.Max(0.02f, baseAvoidRadiusMeters);
            repaired.Add(waypoints[0]);
            for (int i = 1; i < waypoints.Count; i++)
                AppendRepairedSegment(
                    repaired,
                    repaired[repaired.Count - 1],
                    waypoints[i],
                    basePosition,
                    avoidRadius,
                    Mathf.Max(0f, detourHeightMeters));

            return repaired;
        }

        public static float PlanarSegmentDistanceToPoint(
            Vector3 a, Vector3 b, Vector3 point)
        {
            Vector2 aa = new Vector2(a.x, a.z);
            Vector2 bb = new Vector2(b.x, b.z);
            Vector2 pp = new Vector2(point.x, point.z);
            Vector2 ab = bb - aa;
            float denom = ab.sqrMagnitude;
            if (denom < 1e-8f) return Vector2.Distance(aa, pp);
            float t = Mathf.Clamp01(Vector2.Dot(pp - aa, ab) / denom);
            return Vector2.Distance(aa + ab * t, pp);
        }

        private static void AppendRepairedSegment(
            List<Vector3> output,
            Vector3 from,
            Vector3 to,
            Vector3 basePosition,
            float avoidRadius,
            float detourHeight)
        {
            if (!NeedsBaseDetour(from, to, basePosition, avoidRadius))
            {
                AddIfDistinct(output, to);
                return;
            }

            float safeRadius = Mathf.Max(
                avoidRadius * SafeRadiusScale,
                avoidRadius + ExtraSafeRadiusMeters);
            float y = Mathf.Max(from.y, to.y) + detourHeight;
            Vector2 start = new Vector2(from.x - basePosition.x,
                from.z - basePosition.z);
            Vector2 end = new Vector2(to.x - basePosition.x,
                to.z - basePosition.z);
            if (start.sqrMagnitude < 1e-8f || end.sqrMagnitude < 1e-8f)
            {
                AddIfDistinct(output, to);
                return;
            }

            float startDeg = Mathf.Atan2(start.y, start.x) * Mathf.Rad2Deg;
            float endDeg = Mathf.Atan2(end.y, end.x) * Mathf.Rad2Deg;
            float deltaDeg = Mathf.DeltaAngle(startDeg, endDeg);
            if (Mathf.Abs(deltaDeg) < 1e-3f)
                deltaDeg = 180f;

            int steps = Mathf.Max(
                2,
                Mathf.CeilToInt(Mathf.Abs(deltaDeg) / MaxArcStepDegrees));
            for (int step = 0; step <= steps; step++)
            {
                float angleDeg = startDeg + deltaDeg * (step / (float)steps);
                AddIfDistinct(output, PointOnBaseArc(
                    basePosition, safeRadius, angleDeg, y));
            }

            AddIfDistinct(output, to);
        }

        private static bool NeedsBaseDetour(
            Vector3 from,
            Vector3 to,
            Vector3 basePosition,
            float avoidRadius)
        {
            float fromR = PlanarDistance(from, basePosition);
            float toR = PlanarDistance(to, basePosition);
            if (fromR <= avoidRadius || toR <= avoidRadius)
                return false;
            return PlanarSegmentDistanceToPoint(from, to, basePosition) <
                   avoidRadius;
        }

        private static Vector3 PointOnBaseArc(
            Vector3 basePosition,
            float radius,
            float angleDeg,
            float y)
        {
            float rad = angleDeg * Mathf.Deg2Rad;
            return new Vector3(
                basePosition.x + Mathf.Cos(rad) * radius,
                y,
                basePosition.z + Mathf.Sin(rad) * radius);
        }

        private static float PlanarDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static void AddIfDistinct(List<Vector3> output, Vector3 point)
        {
            if (output.Count > 0 &&
                Vector3.Distance(output[output.Count - 1], point) < 0.002f)
                return;
            output.Add(point);
        }
    }
}
