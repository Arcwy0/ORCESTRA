using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace VRInteraction.AI.Speech
{
    public static class WavEncoder
    {
        public static byte[] EncodeMono16(float[] samples, int sampleRate)
        {
            if (samples == null) samples = Array.Empty<float>();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                int dataBytes = samples.Length * 2;
                WriteAscii(writer, "RIFF");
                writer.Write(36 + dataBytes);
                WriteAscii(writer, "WAVE");
                WriteAscii(writer, "fmt ");
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                WriteAscii(writer, "data");
                writer.Write(dataBytes);

                foreach (float f in samples)
                {
                    float clamped = Mathf.Clamp(f, -1f, 1f);
                    writer.Write((short)Mathf.RoundToInt(clamped * short.MaxValue));
                }
                return stream.ToArray();
            }
        }

        private static void WriteAscii(BinaryWriter writer, string value)
        {
            writer.Write(Encoding.ASCII.GetBytes(value));
        }
    }
}
