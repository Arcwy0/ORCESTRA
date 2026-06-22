using System.Collections;
using System.IO;
using System.Net;
using UnityEngine;

public class VoiceRecording : MonoBehaviour
{
    // Variables for recording
    private AudioClip recordedClip;
    private bool isRecording = false;
    private string microphoneDevice;
    private string audioFileName = "recording.wav";
    public AudioSource audioSource;  // Attach AudioSource to play recording

    // Screenshot feedback (attach a UI image or sound effect)
    public GameObject screenshotConfirmationUI;  // Visual cue
    public AudioClip screenshotSoundEffect;  // Sound cue for screenshot confirmation

    // Controller button (Oculus button mappings)
    //public OVRInput.Button recordButton = OVRInput.Button.PrimaryIndexTrigger;

    /*void Update()
    {
        // Start recording when button is pressed
        if (OVRInput.GetDown(recordButton))
        {
            StartRecording();
        }

        // Stop recording when button is released
        if (OVRInput.GetUp(recordButton))
        {
            StopRecording();
        }
    }*/

    // Start the voice recording and capture the screenshot
    private void StartRecording()
    {
        if (!isRecording)
        {
            // Screenshot capture
            StartCoroutine(CaptureScreenshot());

            // Start recording audio
            microphoneDevice = Microphone.devices[0];  // Using the first microphone device
            recordedClip = Microphone.Start(microphoneDevice, false, 30, 16000);  // 16kHz, max 30s
            isRecording = true;
        }
    }

    // Stop recording and save the file
    private void StopRecording()
    {
        if (isRecording)
        {
            Microphone.End(microphoneDevice);  // Stop recording
            SaveRecording(recordedClip);
            isRecording = false;
        }
    }

    // Capture the screenshot and provide feedback
    IEnumerator CaptureScreenshot()
    {
        // Wait until the end of the frame before capturing
        yield return new WaitForEndOfFrame();

        // Save screenshot
        string screenshotPath = Path.Combine(Application.persistentDataPath, "screenshot.png");
        ScreenCapture.CaptureScreenshot(screenshotPath);

        // Provide confirmation feedback (optional)
        if (screenshotConfirmationUI != null)
        {
            screenshotConfirmationUI.SetActive(true);  // Show visual cue
            StartCoroutine(HideConfirmationUI());  // Hide it after 1 second
        }
        if (screenshotSoundEffect != null)
        {
            audioSource.PlayOneShot(screenshotSoundEffect);  // Play sound cue
        }
    }

    // Hide the screenshot confirmation UI after a delay
    IEnumerator HideConfirmationUI()
    {
        yield return new WaitForSeconds(1.0f);
        screenshotConfirmationUI.SetActive(false);
    }

    // Save the recorded audio as a WAV file
    private void SaveRecording(AudioClip clip)
    {
        if (clip == null)
        {
            Debug.LogError("No audio recorded.");
            return;
        }

        string filePath = Path.Combine(Application.persistentDataPath, audioFileName);
        Debug.Log("Saving recording to " + filePath);

        // Convert AudioClip to WAV format
        byte[] wavFile = WavUtility.FromAudioClip(clip);
        File.WriteAllBytes(filePath, wavFile);
    }
}
