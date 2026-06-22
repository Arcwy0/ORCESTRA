using UnityEngine;
    
public class PlaceObject : MonoBehaviour
{
    public string targetTag;  // Tag of the correct target location
    public string taskDescription;  // Description of the task to complete when placed correctly
    public TaskManager taskManager;
    public AudioClip successClip;  // Sound to play on successful placement
    public AudioClip failClip;  // Sound to play on failure
    private AudioSource audioSource;

    void Start()
    {
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null) { audioSource = gameObject.AddComponent<AudioSource>(); }
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag(targetTag))
        {
            bool taskCompleted = taskManager.CompleteTask(taskDescription);
            if (taskCompleted)
            {
                audioSource.PlayOneShot(successClip);
            }
            else
            {
                audioSource.PlayOneShot(failClip);
            }
        }
    }
}