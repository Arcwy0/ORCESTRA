using System;
using System.IO;
using UnityEngine;

namespace VRInteraction.AI
{
    public static class AiGroundingDebugImageWriter
    {
        private const int CrossRadius = 10;

        public static bool TrySave(
            AiImageCapture capture, AiCommandResponse response, Camera cam,
            out string path, out string error)
        {
            path = null;
            error = null;
            if (capture == null || capture.texture == null)
            {
                error = "No captured image texture.";
                return false;
            }

            try
            {
                var tex = new Texture2D(
                    capture.texture.width, capture.texture.height,
                    TextureFormat.RGBA32, false);
                tex.SetPixels32(capture.texture.GetPixels32());

                int width = tex.width;
                int height = tex.height;
                DrawGroundings(tex, response, width, height);
                DrawWaypointProjections(tex, capture, response, cam, width, height);
                tex.Apply(false, false);

                string dir = Path.Combine(
                    Application.persistentDataPath,
                    "RobotAI", "grounding_debug");
                Directory.CreateDirectory(dir);
                string requestId = response != null &&
                                   response.diagnostics != null &&
                                   !string.IsNullOrEmpty(
                                       response.diagnostics.request_id)
                    ? Sanitize(response.diagnostics.request_id)
                    : "request";
                string source = !string.IsNullOrEmpty(capture.source)
                    ? Sanitize(capture.source)
                    : "image";
                path = Path.Combine(
                    dir,
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") +
                    "_" + requestId + "_" + source + "_grounding.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static void DrawGroundings(
            Texture2D tex, AiCommandResponse response, int width, int height)
        {
            foreach (var grounding in Groundings(response))
            {
                if (grounding == null) continue;
                if (grounding.bbox_xyxy_px != null &&
                    grounding.bbox_xyxy_px.Length >= 4)
                {
                    DrawRect(tex,
                        grounding.bbox_xyxy_px[0],
                        grounding.bbox_xyxy_px[1],
                        grounding.bbox_xyxy_px[2],
                        grounding.bbox_xyxy_px[3],
                        new Color32(255, 0, 0, 255),
                        width, height);
                }

                if (grounding.preferred_point_px != null &&
                    grounding.preferred_point_px.Length >= 2)
                {
                    DrawCross(tex,
                        new Vector2(
                            grounding.preferred_point_px[0],
                            grounding.preferred_point_px[1]),
                        new Color32(0, 255, 0, 255),
                        width, height, CrossRadius);
                }
            }
        }

        private static void DrawWaypointProjections(
            Texture2D tex, AiImageCapture capture, AiCommandResponse response,
            Camera cam, int width, int height)
        {
            if (response == null || response.plan_ir == null ||
                response.plan_ir.waypoints == null)
                return;

            Vector2? previous = null;
            for (int i = 0; i < response.plan_ir.waypoints.Length; i++)
            {
                var waypoint = response.plan_ir.waypoints[i];
                if (waypoint == null || waypoint.position_m == null ||
                    waypoint.position_m.Length < 3)
                    continue;

                Vector3 world = AiModelUtil.ToVector3(waypoint.position_m);
                if (!TryProjectWorldToTopLeftPixel(
                        world, capture, cam, width, height,
                        out Vector2 pixel, out _))
                    continue;

                bool inside = pixel.x >= 0f && pixel.x < width &&
                              pixel.y >= 0f && pixel.y < height;
                Color32 color = inside
                    ? new Color32(0, 255, 255, 255)
                    : new Color32(255, 0, 255, 255);
                Vector2 clamped = ClampImagePoint(pixel, width, height);
                if (previous.HasValue)
                    DrawLine(tex, previous.Value, clamped,
                        new Color32(255, 255, 255, 255), width, height);
                DrawCircle(tex, clamped, color, width, height, 13);
                DrawCross(tex, clamped, color, width, height, 8);
                previous = clamped;
            }
        }

        private static bool TryProjectWorldToTopLeftPixel(
            Vector3 world, AiImageCapture capture, Camera cam,
            int imageWidth, int imageHeight, out Vector2 pixel,
            out string error)
        {
            pixel = Vector2.zero;
            error = null;

            if (capture != null &&
                capture.rayProvider is IAiImageProjectionProvider provider &&
                provider.TryProjectWorldToTopLeftPixel(
                    world, imageWidth, imageHeight, out pixel, out error))
                return true;

            if (AiGroundingService.IsQuestPassthroughCapture(capture))
            {
                if (string.IsNullOrEmpty(error))
                    error = "Quest passthrough image has no PCA projection provider.";
                return false;
            }

            if (cam == null)
            {
                error = "No camera for projection.";
                return false;
            }

            Vector3 screen = cam.WorldToScreenPoint(world);
            if (screen.z <= 0f || !AiModelUtil.IsFinite(screen))
            {
                error = "World point is behind the Unity camera.";
                return false;
            }

            float sx = imageWidth / Mathf.Max(1f, cam.pixelWidth);
            float sy = imageHeight / Mathf.Max(1f, cam.pixelHeight);
            pixel = new Vector2(screen.x * sx, (cam.pixelHeight - screen.y) * sy);
            return true;
        }

        private static AiVisualGrounding[] Groundings(AiCommandResponse response)
        {
            if (response == null) return Array.Empty<AiVisualGrounding>();
            if (response.visual_groundings != null &&
                response.visual_groundings.Length > 0)
                return response.visual_groundings;
            return response.visual_grounding != null
                ? new[] { response.visual_grounding }
                : Array.Empty<AiVisualGrounding>();
        }

        private static void DrawRect(
            Texture2D tex, float x1, float y1, float x2, float y2,
            Color32 color, int width, int height)
        {
            Vector2 a = ClampImagePoint(new Vector2(Mathf.Min(x1, x2), Mathf.Min(y1, y2)), width, height);
            Vector2 b = ClampImagePoint(new Vector2(Mathf.Max(x1, x2), Mathf.Max(y1, y2)), width, height);
            DrawLine(tex, new Vector2(a.x, a.y), new Vector2(b.x, a.y), color, width, height);
            DrawLine(tex, new Vector2(b.x, a.y), new Vector2(b.x, b.y), color, width, height);
            DrawLine(tex, new Vector2(b.x, b.y), new Vector2(a.x, b.y), color, width, height);
            DrawLine(tex, new Vector2(a.x, b.y), new Vector2(a.x, a.y), color, width, height);
        }

        private static void DrawCross(
            Texture2D tex, Vector2 p, Color32 color,
            int width, int height, int radius)
        {
            Vector2 c = ClampImagePoint(p, width, height);
            DrawLine(tex, new Vector2(c.x - radius, c.y),
                new Vector2(c.x + radius, c.y), color, width, height);
            DrawLine(tex, new Vector2(c.x, c.y - radius),
                new Vector2(c.x, c.y + radius), color, width, height);
        }

        private static void DrawCircle(
            Texture2D tex, Vector2 p, Color32 color,
            int width, int height, int radius)
        {
            Vector2 c = ClampImagePoint(p, width, height);
            int steps = Mathf.Max(16, radius * 4);
            Vector2 prev = c + new Vector2(radius, 0f);
            for (int i = 1; i <= steps; i++)
            {
                float a = i / (float)steps * Mathf.PI * 2f;
                Vector2 next = c + new Vector2(
                    Mathf.Cos(a) * radius,
                    Mathf.Sin(a) * radius);
                DrawLine(tex, prev, next, color, width, height);
                prev = next;
            }
        }

        private static void DrawLine(
            Texture2D tex, Vector2 a, Vector2 b, Color32 color,
            int width, int height)
        {
            int x0 = Mathf.RoundToInt(a.x);
            int y0 = Mathf.RoundToInt(a.y);
            int x1 = Mathf.RoundToInt(b.x);
            int y1 = Mathf.RoundToInt(b.y);
            int dx = Mathf.Abs(x1 - x0);
            int sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0);
            int sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            while (true)
            {
                SetTopLeftPixel(tex, x0, y0, color, width, height);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy)
                {
                    err += dy;
                    x0 += sx;
                }
                if (e2 <= dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        private static void SetTopLeftPixel(
            Texture2D tex, int x, int y, Color32 color, int width, int height)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return;
            tex.SetPixel(x, height - 1 - y, color);
        }

        private static Vector2 ClampImagePoint(
            Vector2 p, int width, int height)
        {
            return new Vector2(
                Mathf.Clamp(p.x, 0f, Mathf.Max(0f, width - 1f)),
                Mathf.Clamp(p.y, 0f, Mathf.Max(0f, height - 1f)));
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "value";
            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value;
        }
    }
}
