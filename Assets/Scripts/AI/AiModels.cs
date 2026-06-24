using System;
using UnityEngine;

namespace VRInteraction.AI
{
    public enum AiImageSourceMode
    {
        UnityScreenshot,
        QuestPassthroughCamera
    }

    public static class AiImageCaptureFallbackPolicy
    {
        public static bool ShouldUseScreenshotFallback(AiImageSourceMode mode)
        {
            return mode == AiImageSourceMode.UnityScreenshot;
        }
    }

    public class AiImageCapture
    {
        public Texture2D texture;
        public string source;
        public int width;
        public int height;
        public string error;
        public bool hasCameraMatrices;
        public Matrix4x4 worldFromCamera;
        public Matrix4x4 projection;
        public IAiImageRayProvider rayProvider;
    }

    public interface IAiImageRayProvider
    {
        string RaySource { get; }

        bool TryCreateRay(
            Vector2 topLeftPixel, int imageWidth, int imageHeight,
            out Ray ray, out string error);
    }

    public interface IAiImageProjectionProvider
    {
        bool TryProjectWorldToTopLeftPixel(
            Vector3 worldPosition, int imageWidth, int imageHeight,
            out Vector2 topLeftPixel, out string error);
    }

    public static class AiImageRayUtil
    {
        public static Vector2 TopLeftPixelToViewport(
            Vector2 topLeftPixel, int imageWidth, int imageHeight)
        {
            float width = Mathf.Max(1f, imageWidth);
            float height = Mathf.Max(1f, imageHeight);
            return new Vector2(
                Mathf.Clamp01(topLeftPixel.x / width),
                Mathf.Clamp01(1f - topLeftPixel.y / height));
        }

        public static Vector2 ViewportToTopLeftPixel(
            Vector2 viewport, int imageWidth, int imageHeight)
        {
            float width = Mathf.Max(1f, imageWidth);
            float height = Mathf.Max(1f, imageHeight);
            return new Vector2(
                viewport.x * width,
                (1f - viewport.y) * height);
        }
    }

    public static class AiPassthroughFrameGate
    {
        public static bool IsFresh(DateTime currentTimestamp,
            DateTime lastCapturedTimestamp)
        {
            return currentTimestamp != default &&
                   currentTimestamp != lastCapturedTimestamp;
        }

        public static bool IsFreshForRequest(DateTime currentTimestamp,
            DateTime lastCapturedTimestamp, DateTime requestStartTimestamp)
        {
            return IsFresh(currentTimestamp, lastCapturedTimestamp) &&
                   (requestStartTimestamp == default ||
                    currentTimestamp != requestStartTimestamp);
        }
    }

    [Serializable]
    public class AiCommandRequest
    {
        public string session_id;
        public string command_text;
        public string image_source;
        public string audio_format;
        public int audio_sample_rate_hz;
        public AiCameraSnapshot camera;
        public AiRobotSnapshot[] robots;
        public AiPlaneSnapshot[] mr_planes;
        public AiKnownSceneObject[] known_scene_objects;
    }

    [Serializable]
    public class AiCameraSnapshot
    {
        public float[] world_from_camera;
        public float[] projection;
        public int width;
        public int height;
    }

    [Serializable]
    public class AiRobotSnapshot
    {
        public string id;
        public string kind;
        public float[] root_position_m;
        public float[] root_rotation_xyzw;
        public float[] tcp_position_m;
        public float[] reach_center_m;
        public float reach_radius_m;
        public float[] joint_deg;
    }

    [Serializable]
    public class AiPlaneSnapshot
    {
        public string id;
        public float[] center_m;
        public float[] normal;
        public float[] extent_m;
    }

    [Serializable]
    public class AiKnownSceneObject
    {
        public string id;
        public string label;
        public float[] position_m;
        public float[] size_m;
    }

    [Serializable]
    public class AiCommandResponse
    {
        public string spoken_reply;
        public AiIntent intent;
        public AiVisualGrounding visual_grounding;
        public AiVisualGrounding[] visual_groundings;
        public AiPlanIr plan_ir;
        public AiDiagnostics diagnostics;
        public AiError error;
    }

    [Serializable]
    public class AiIntent
    {
        public string robot_id;
        public string task_type;
        public string target_ref;
        public string motion_primitive;
    }

    [Serializable]
    public class AiVisualGrounding
    {
        public string label;
        public float confidence;
        public float[] bbox_xyxy_px;
        public float[] preferred_point_px;
        public float[] world_position_m;
        public float world_confidence;
    }

    [Serializable]
    public class AiPlanIr
    {
        public int version = 1;
        public string kind;
        public string robot_id;
        public bool requires_confirmation = true;
        public bool contact_allowed;
        public float min_clearance_m = 0.05f;
        public float speed_scale = 0.25f;
        public AiWaypoint[] waypoints;
    }

    [Serializable]
    public class AiWaypoint
    {
        public float[] position_m;
        public float[] tcp_orientation_xyzw;
        public float speed_scale = 0.25f;
        public float dwell_s;
    }

    [Serializable]
    public class AiError
    {
        public string code;
        public string message;
    }

    [Serializable]
    public class AiDiagnostics
    {
        public string request_id;
        public string gateway_mode;
        public float gateway_latency_ms;
        public string asr_mode;
        public float asr_latency_ms;
        public int audio_bytes;
        public string transcript_text;
        public string image_sha256_12;
        public string saved_image_path;
        public string saved_annotated_image_path;
        public string saved_trace_path;
        public string rejection_reason;
    }

    [Serializable]
    public class AiTranscribeResponse
    {
        public string text;
        public AiDiagnostics diagnostics;
        public AiError error;
    }

    public static class AiModelUtil
    {
        public static float[] Vec3(Vector3 v) =>
            new[] { v.x, v.y, v.z };

        public static float[] Quat(Quaternion q) =>
            new[] { q.x, q.y, q.z, q.w };

        public static Vector3 ToVector3(float[] v)
        {
            if (v == null || v.Length < 3) return Vector3.zero;
            return new Vector3(v[0], v[1], v[2]);
        }

        public static float[] Matrix(Matrix4x4 m)
        {
            var a = new float[16];
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    a[row * 4 + col] = m[row, col];
            return a;
        }

        public static bool IsFinite(Vector3 v) =>
            IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);

        public static bool IsFinite(Vector2 v) =>
            IsFinite(v.x) && IsFinite(v.y);

        public static bool IsFinite(float v) =>
            !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
