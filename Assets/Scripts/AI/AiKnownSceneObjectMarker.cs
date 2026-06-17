using UnityEngine;

namespace VRInteraction.AI
{
    public class AiKnownSceneObjectMarker : MonoBehaviour
    {
        public string objectId = "target";
        public string label = "red cube";
        public Vector3 sizeMeters = new Vector3(0.1f, 0.1f, 0.1f);

        private void Reset()
        {
            objectId = gameObject.name;
            label = gameObject.name.ToLowerInvariant();
            var col = GetComponent<Collider>();
            if (col != null) sizeMeters = col.bounds.size;
        }
    }
}
