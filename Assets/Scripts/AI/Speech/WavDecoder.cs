using System;
using System.Text;

namespace VRInteraction.AI.Speech
{
    public static class WavDecoder
    {
        public static bool TryDecodeMono16(byte[] wavBytes, out float[] samples,
            out int sampleRateHz, out string error)
        {
            samples = Array.Empty<float>();
            sampleRateHz = 0;
            error = null;

            if (wavBytes == null || wavBytes.Length < 44)
            {
                error = "WAV data is empty or too short.";
                return false;
            }
            if (ReadAscii(wavBytes, 0, 4) != "RIFF" ||
                ReadAscii(wavBytes, 8, 4) != "WAVE")
            {
                error = "Audio is not a RIFF/WAVE file.";
                return false;
            }

            int offset = 12;
            int channels = 0;
            int bitsPerSample = 0;
            int dataOffset = -1;
            int dataSize = 0;

            while (offset + 8 <= wavBytes.Length)
            {
                string chunkId = ReadAscii(wavBytes, offset, 4);
                int chunkSize = ReadInt32LE(wavBytes, offset + 4);
                int chunkData = offset + 8;
                if (chunkSize < 0 || chunkData + chunkSize > wavBytes.Length)
                    break;

                if (chunkId == "fmt ")
                {
                    short format = ReadInt16LE(wavBytes, chunkData);
                    channels = ReadInt16LE(wavBytes, chunkData + 2);
                    sampleRateHz = ReadInt32LE(wavBytes, chunkData + 4);
                    bitsPerSample = ReadInt16LE(wavBytes, chunkData + 14);
                    if (format != 1)
                    {
                        error = "Only PCM WAV is supported.";
                        return false;
                    }
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkData;
                    dataSize = chunkSize;
                }

                offset = chunkData + chunkSize + (chunkSize & 1);
            }

            if (dataOffset < 0 || dataSize <= 0)
            {
                error = "WAV data chunk is missing.";
                return false;
            }
            if (channels <= 0 || sampleRateHz <= 0)
            {
                error = "WAV format chunk is missing.";
                return false;
            }
            if (bitsPerSample != 16)
            {
                error = "Only 16-bit PCM WAV is supported.";
                return false;
            }

            int frames = dataSize / (channels * 2);
            samples = new float[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                int frameOffset = dataOffset + frame * channels * 2;
                int sum = 0;
                for (int ch = 0; ch < channels; ch++)
                    sum += ReadInt16LE(wavBytes, frameOffset + ch * 2);
                samples[frame] = (sum / (float)channels) / 32768f;
            }
            return true;
        }

        private static string ReadAscii(byte[] data, int offset, int count)
        {
            return Encoding.ASCII.GetString(data, offset, count);
        }

        private static short ReadInt16LE(byte[] data, int offset)
        {
            return (short)(data[offset] | (data[offset + 1] << 8));
        }

        private static int ReadInt32LE(byte[] data, int offset)
        {
            return data[offset] | (data[offset + 1] << 8) |
                   (data[offset + 2] << 16) | (data[offset + 3] << 24);
        }
    }
}
