using System.Collections.Generic;
using UnityEngine;
using Unity.Sentis;
using System.IO;
using System.Text;

public class RunWhisper : MonoBehaviour
{
    public ServerCommunication serverComm;  // Reference to the ServerCommunication script

    IWorker decoderEngine, encoderEngine, spectroEngine;

    const BackendType backend = BackendType.GPUCompute;

    private AudioClip recordedAudioClip;
    private float START_TIME = 0f; // Define the start time for speech recognition
    private char[] whiteSpaceCharacters = new char[] { ' ', '\n', '\r', '\t' }; // Define whitespace characters for trimming

    const int maxTokens = 100;

    const int END_OF_TEXT = 50257;
    const int START_OF_TRANSCRIPT = 50258;
    const int ENGLISH = 50259;
    const int TRANSCRIBE = 50359;
    const int NO_TIME_STAMPS = 50363;

    int numSamples;
    float[] data;
    string[] tokens;

    int currentToken = 0;
    int[] outputTokens = new int[maxTokens];

    TensorFloat encodedAudio;

    bool transcribe = false;
    string outputString = "";

    const int maxSamples = 30 * 16000;

    void Start()
    {
        SetupWhiteSpaceShifts();
        GetTokens();

        Model decoder = ModelLoader.Load(Application.streamingAssetsPath + "/AudioDecoder_Tiny.sentis");
        Model decoderWithArgMax = Functional.Compile(
            (tokens, audio) => Functional.ArgMax(decoder.Forward(tokens, audio)[0], 2),
            (decoder.inputs[0], decoder.inputs[1])
        );
        Model encoder = ModelLoader.Load(Application.streamingAssetsPath + "/AudioEncoder_Tiny.sentis");
        Model spectro = ModelLoader.Load(Application.streamingAssetsPath + "/LogMelSepctro.sentis");

        decoderEngine = WorkerFactory.CreateWorker(backend, decoderWithArgMax);
        encoderEngine = WorkerFactory.CreateWorker(backend, encoder);
        spectroEngine = WorkerFactory.CreateWorker(backend, spectro);

        outputTokens[0] = START_OF_TRANSCRIPT;
        outputTokens[1] = ENGLISH;
        outputTokens[2] = TRANSCRIBE;
        outputTokens[3] = NO_TIME_STAMPS;
        currentToken = 3;
    }

    // Called when a new recording is available
    public void OnNewRecording(AudioClip clip)
    {
        recordedAudioClip = clip;
        Debug.Log("New audio clip received. Starting transcription...");
        LoadAudio();
        EncodeAudio();
        transcribe = true;
    }

    void LoadAudio()
    {
        if (recordedAudioClip.frequency != 16000)
        {
            Debug.Log($"The audio clip should have frequency 16kHz. It has frequency {recordedAudioClip.frequency / 1000f}kHz");
            return;
        }

        numSamples = recordedAudioClip.samples;
        if (numSamples > maxSamples)
        {
            Debug.Log($"The AudioClip is too long. It must be less than 30 seconds. This clip is {numSamples / recordedAudioClip.frequency} seconds.");
            return;
        }

        data = new float[maxSamples];
        recordedAudioClip.GetData(data, 0);
    }

    void GetTokens()
    {
        var jsonText = File.ReadAllText(Application.streamingAssetsPath + "/vocab.json");
        var vocab = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, int>>(jsonText);
        tokens = new string[vocab.Count];
        foreach (var item in vocab)
        {
            tokens[item.Value] = item.Key;
        }
    }

    void EncodeAudio()
    {
        using var input = new TensorFloat(new TensorShape(1, numSamples), data);

        spectroEngine.Execute(input);
        var spectroOutput = spectroEngine.PeekOutput() as TensorFloat;

        encoderEngine.Execute(spectroOutput);
        encodedAudio = encoderEngine.PeekOutput() as TensorFloat;
    }

    void Update()
    {
        if (transcribe && currentToken < outputTokens.Length - 1)
        {
            using var tokensSoFar = new TensorInt(new TensorShape(1, outputTokens.Length), outputTokens);

            var inputs = new Dictionary<string, Tensor>
            {
                {"input_0", tokensSoFar },
                {"input_1", encodedAudio }
            };

            decoderEngine.Execute(inputs);
            var tokensPredictions = decoderEngine.PeekOutput() as TensorInt;

            tokensPredictions.CompleteOperationsAndDownload();

            int ID = tokensPredictions[currentToken];

            outputTokens[++currentToken] = ID;

            if (ID == END_OF_TEXT)
            {
                transcribe = false;
                Debug.Log("Transcription complete. Sending to VLM server...");

                // Capture a screenshot
                Texture2D screenshot = ScreenCapture.CaptureScreenshotAsTexture();

                // Send the recognized text and screenshot to VLM server
                serverComm.SendVLMRequest(outputString, screenshot);
            }
            else if (ID >= tokens.Length)
            {
                outputString += $"(time={(ID - START_TIME) * 0.02f})";
            }
            else outputString += GetUnicodeText(tokens[ID]);

            Debug.Log(outputString);
        }
    }

    string GetUnicodeText(string text)
    {
        var bytes = Encoding.GetEncoding("ISO-8859-1").GetBytes(ShiftCharacterDown(text));
        return Encoding.UTF8.GetString(bytes);
    }

    string ShiftCharacterDown(string text)
    {
        string outText = "";
        foreach (char letter in text)
        {
            outText += ((int)letter <= 256) ? letter :
                (char)whiteSpaceCharacters[(int)(letter - 256)];
        }
        return outText;
    }

    void SetupWhiteSpaceShifts()
    {
        whiteSpaceCharacters = new char[256]; // Re-define the whiteSpaceCharacters properly
        for (int i = 0, n = 0; i < 256; i++)
        {
            if (IsWhiteSpace((char)i)) whiteSpaceCharacters[n++] = (char)i;
        }
    }

    bool IsWhiteSpace(char c)
    {
        return !(('!' <= c && c <= '~') || ('¡' <= c && c <= '¬') || ('®' <= c && c <= 'ÿ'));
    }

    private void OnDestroy()
    {
        decoderEngine?.Dispose();
        encoderEngine?.Dispose();
        spectroEngine?.Dispose();
    }
}