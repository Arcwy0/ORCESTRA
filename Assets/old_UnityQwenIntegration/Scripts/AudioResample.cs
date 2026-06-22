using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Sentis;

/*
 *    Model to turn 44kHz and 22kHz audio to 16kHz 
 *    ============================================
 *    
 *  Place the audioClip in the inputAudio field and press play 
 *  The results will appear in the console
 */

public class AudioResample : MonoBehaviour
{
    //Place the audio clip to resample here
    public AudioClip inputAudio;

    public AudioClip outputAudio;

    public bool playFinalAudio = true;

    IWorker engine;
    IBackend backend;

    BackendType backendType = BackendType.GPUCompute;

    void Start()
    {
        backend = WorkerFactory.CreateBackend(backendType);
        ConvertAudio();
    }

    // Update is called once per frame
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            ConvertAudio();
        }
    }

    void ConvertAudio()
    {
        Debug.Log($"The frequency of the input audio clip is {inputAudio.frequency} Hz with {inputAudio.channels} channels.");
        Model model;
        if (inputAudio.frequency == 44100)
        {
            model = ModelLoader.Load(Application.streamingAssetsPath + "/audio_resample_44100_16000.sentis");
        }
        else if (inputAudio.frequency == 22050)
        {
            model = ModelLoader.Load(Application.streamingAssetsPath + "/audio_resample_22050_16000.sentis");
        }
        else
        {
            Debug.Log("Only frequencies of 44kHz and 22kHz are compatible");
            return;
        }

        engine = WorkerFactory.CreateWorker(backendType, model);

        int channels = inputAudio.channels;
        int size = inputAudio.samples * channels;
        float[] data = new float[size];
        inputAudio.GetData(data, 0);
        using var input = new TensorFloat(new TensorShape(1, size), data);

        engine.Execute(input);

        float[] outData;

        var output = engine.PeekOutput() as TensorFloat;
        if (inputAudio.frequency == 44100)
        {
            //The model gives 2x as many samples as we would like so we fix it:
            //We need to pad it if it has odd number of samples
            int n = output.shape[1] % 2;
            using var output2 = TensorFloat.AllocNoData(new TensorShape(1, output.shape[1] + n));
            backend.Pad(output, output2, new int[] { 0, n }, Unity.Sentis.Layers.PadMode.Constant, 0);
            
            //Now we take every second sample:
            output2.Reshape(new TensorShape(output2.shape[1] / 2, 2));
            using var output3 = TensorFloat.AllocNoData(new TensorShape(output2.shape[0], 1));
            backend.Slice(output2, output3, new[] { 0 }, new[] { 1 }, new[] { 1 });
            output3.CompleteOperationsAndDownload();
            outData = output3.ToReadOnlyArray();
        }
        else
        {
            output.CompleteOperationsAndDownload();
            outData = output.ToReadOnlyArray();
        }

        int samplesOut = outData.Length / channels;

        outputAudio = AudioClip.Create("outputAudio1", samplesOut, channels, 16000, false);
        outputAudio.SetData(outData, 0);

        Debug.Log($"The audio has been converted to 16Khz with {channels} channels.");

        if (playFinalAudio)
        {
            GetComponent<AudioSource>().PlayOneShot(outputAudio);
        }
    }

    private void OnDestroy()
    {
        engine?.Dispose();
        backend?.Dispose();
    }
}