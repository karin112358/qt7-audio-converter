using System;
using System.IO;
using System.Text;

namespace Qt7AudioConverter
{
    /// <summary>
    /// Writes canonical QuickTime 7-style WAV output (RIFF → fmt → data,
    /// 16-bit PCM) with size fields patched after the data is known.
    /// </summary>
    internal static class CanonicalWavWriter
    {
        /// <summary>Sample rate every converted (non-lossless) output uses.</summary>
        public const int TargetSampleRate = 44100;

        /// <summary>Writes the RIFF/fmt/data headers with zeroed size fields;
        /// returns the header start position for <see cref="PatchSizes"/>.</summary>
        public static long WriteHeader(Stream output, int sampleRate, int channels)
        {
            long headerStart = output.Position;
            var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
            int blockAlign = channels * 2; // 16-bit PCM
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(0u); // RIFF size, patched later
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16u);
            writer.Write((ushort)1); // PCM
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write((uint)(sampleRate * blockAlign)); // byte rate
            writer.Write((ushort)blockAlign);
            writer.Write((ushort)16); // bits per sample
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(0u); // data size, patched later
            writer.Flush();
            return headerStart;
        }

        /// <summary>Converts float samples (clamped to ±1) to 16-bit little-endian
        /// PCM and writes them; returns the number of bytes written.</summary>
        public static long WritePcm16(Stream output, float[] samples, int count, ref byte[] pcm)
        {
            if (count == 0) return 0;
            if (pcm.Length < count * 2) pcm = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                float clamped = samples[i];
                if (clamped > 1f) clamped = 1f;
                else if (clamped < -1f) clamped = -1f;
                short sample = (short)Math.Round(clamped * short.MaxValue);
                pcm[i * 2] = (byte)sample;
                pcm[i * 2 + 1] = (byte)(sample >> 8);
            }
            output.Write(pcm, 0, count * 2);
            return count * 2;
        }

        /// <summary>Patches the RIFF and data size fields and seeks back to the
        /// end of the stream. 16-bit data is always even, so no pad byte.</summary>
        public static void PatchSizes(Stream output, long headerStart, long dataBytes)
        {
            if (dataBytes == 0)
                throw new InvalidDataException("No audio data could be decoded.");
            if (dataBytes + 36 > uint.MaxValue)
                throw new InvalidDataException("Decoded audio exceeds the 4 GB WAV limit.");

            var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
            output.Seek(headerStart + 4, SeekOrigin.Begin);
            writer.Write((uint)(36 + dataBytes)); // RIFF size
            output.Seek(headerStart + 40, SeekOrigin.Begin);
            writer.Write((uint)dataBytes);
            writer.Flush();
            output.Seek(0, SeekOrigin.End);
        }
    }
}
