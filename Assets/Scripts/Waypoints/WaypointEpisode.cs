using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VRInteraction.Waypoints
{
    /// <summary>
    /// A saved waypoint scenario ("episode"). Stores everything needed to
    /// reproduce a taught path later, independent of how the robot has been
    /// moved since:
    ///   • the robot's recorded START state (manipulator joint angles, or the
    ///     mobile base pose), which playback returns to FIRST;
    ///   • the waypoints in the robot's RECORD-TIME base frame (relative), so
    ///     they reattach correctly to the robot on load.
    /// </summary>
    [System.Serializable]
    public class WaypointEpisode
    {
        public string name;
        public string robotName;      // PlacedRobot.displayName at record time
        public int kind;              // (int)RobotKind  0=Manipulator 1=Mobile
        public Vector3 basePos;       // robot root world position at record
        public Quaternion baseRot;    // robot root world rotation at record
        public float[] jointDegs;     // manipulator start joints (6) or empty
        public Vector3[] localPoints; // waypoints relative to the record base frame
        public float playbackRate;
        public string savedUtc;
    }

    /// <summary>
    /// Reads/writes <see cref="WaypointEpisode"/> JSON files under
    /// <c>persistentDataPath/Episodes</c> (works in the Editor and in builds).
    /// </summary>
    public static class EpisodeStore
    {
        private static string Dir =>
            Path.Combine(Application.persistentDataPath, "Episodes");

        public static string Save(WaypointEpisode ep)
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, Sanitize(ep.name) + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(ep, true));
            return path;
        }

        public static List<WaypointEpisode> LoadAll()
        {
            var list = new List<WaypointEpisode>();
            if (!Directory.Exists(Dir)) return list;
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
            {
                try
                {
                    var ep = JsonUtility.FromJson<WaypointEpisode>(
                        File.ReadAllText(f));
                    if (ep != null) list.Add(ep);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[EpisodeStore] Skipped '{f}': {e.Message}");
                }
            }
            return list;
        }

        public static void Delete(string name)
        {
            string path = Path.Combine(Dir, Sanitize(name) + ".json");
            if (File.Exists(path)) File.Delete(path);
        }

        public static string Folder => Dir;

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "episode";
            foreach (var ch in Path.GetInvalidFileNameChars())
                s = s.Replace(ch, '_');
            return s.Trim();
        }
    }
}
