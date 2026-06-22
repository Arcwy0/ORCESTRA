using System;
using System.IO;
using UnityEngine;

public static class WavUtility
{
    // Convert an AudioClip to a WAV file (byte array)
    public static byte[] FromAudioClip(AudioClip clip)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            WriteWavHeader(stream, clip); WriteWavHeader(stream, clip);

            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);

            Int16[] intData = new Int16[samples.Length];
            Byte[] bytesData = new Byte[samples.Length * 2];

            // Converting the audio samples from float to Int16
            int rescaleFactor = 32767;
            for (int i = 0; i < samples.Length; i++)
            {
                intData[i] = (short)(samples[i] * rescaleFactor);
                Byte[] byteArr = BitConverter.GetBytes(intData[i]);
                byteArr.CopyTo(bytesData, i * 2);
            }

            stream.Write(bytesData, 0, bytesData.Length);

            // Finalize the WAV header
            stream.Seek(4, SeekOrigin.Begin);
            stream.Write(BitConverter.GetBytes((int)stream.Length - 8), 0, 4);  // File size
            stream.Seek(40, SeekOrigin.Begin);
            stream.Write(BitConverter.GetBytes((int)(stream.Length - 44)), 0, 4);  // Data chunk size

            return stream.ToArray();
        }
    }

    // Write the WAV file header
    private static void WriteWavHeader(MemoryStream stream, AudioClip clip)
    {
        int sampleRate = clip.frequency;
        int channels = clip.channels;
        int samples = clip.samples;

        stream.Write(System.Text.Encoding.UTF8.GetBytes("RIFF"), 0, 4);
        stream.Write(new byte[4], 0, 4);  // Placeholder for file size
        stream.Write(System.Text.Encoding.UTF8.GetBytes("WAVE"), 0, 4);
        stream.Write(System.Text.Encoding.UTF8.GetBytes("fmt "), 0, 4);
        stream.Write(BitConverter.GetBytes(16), 0, 4);  // Sub-chunk size (16 for PCM)
        stream.Write(BitConverter.GetBytes((short)1), 0, 2);  // Audio format (1 for PCM)
        stream.Write(BitConverter.GetBytes((short)channels), 0, 2);
        stream.Write(BitConverter.GetBytes(sampleRate), 0, 4);
        stream.Write(BitConverter.GetBytes(sampleRate * channels * 2), 0, 4);  // Byte rate
        stream.Write(BitConverter.GetBytes((short)(channels * 2)), 0, 2);  // Block align
        stream.Write(BitConverter.GetBytes((short)16), 0, 2);  // Bits per sample
        stream.Write(System.Text.Encoding.UTF8.GetBytes("data"), 0, 4);
        stream.Write(new byte[4], 0, 4);  // Placeholder for data chunk size
    }
}