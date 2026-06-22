using UnityEngine;
using UnityEngine.Networking;
using System.Collections;

public class ServerCommunication : MonoBehaviour
{
    public string serverURL = "http://your_server_url";
    public RunJets ttsModule; // Reference to the TTS module

    // This will be triggered once speech recognition is done
    public void SendVLMRequest(string recognizedText, Texture2D screenshot)
    {
        StartCoroutine(SendRequest(screenshot, recognizedText));
    }

    // Send screenshot and text to the VLM server
    private IEnumerator SendRequest(Texture2D screenshot, string text)
    {
        // Convert screenshot to byte array
        byte[] imageBytes = screenshot.EncodeToPNG();

        // Create form data
        WWWForm form = new WWWForm();
        form.AddField("text", text);
        form.AddBinaryData("image", imageBytes, "screenshot.png", "image/png");

        // Send request to VLM server
        using (UnityWebRequest www = UnityWebRequest.Post(serverURL, form))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                string serverResponse = www.downloadHandler.text;
                Debug.Log("Server response: " + serverResponse);

                // Dynamically set the server response text in the TTS module
                ttsModule.SetInputText(serverResponse);
            }
            else
            {
                Debug.LogError("Error sending request: " + www.error);
            }
        }
    }
}