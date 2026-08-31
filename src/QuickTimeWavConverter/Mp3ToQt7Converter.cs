using System;
using System.IO;
using System.Text;
using NLayer;

namespace QuickTimeWavConverter
{
    /// <summary>
    /// Decodes an MP3 file to a canonical QuickTime 7-style WAV
    /// (RIFF → fmt → data, 16-bit PCM). Uses the managed NLayer decoder,
    /// so it works on Windows and macOS without any native dependencies.
    /// </summary>
    public static class Mp3ToQt7Converter
    {
        private const int SamplesPerBlock = 16 * 1024;

        /// <summary>
        /// Decodes <paramref name="inputPath"/> and writes a canonical 16-bit
        /// PCM WAV to <paramref name="outputPath"/> (overwriting it if it exists).
        /// Sample rate and channel count are taken from the MP3.
        /// </summary>
        public static void Convert(string inputPath, string outputPath)
        {
            using (var mpeg = new MpegFile(inputPath))
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                Convert(mpeg, output);
            }
        }

        /// <summary>
        /// Decodes the MP3 in <paramref name="input"/> and writes a canonical
        /// 16-bit PCM WAV to <paramref name="output"/>. The output stream must
        /// be seekable (the size fields are patched after decoding); both
        /// streams are left open.
        /// </summary>
        public static void Convert(Stream input, Stream output)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using (var mpeg = new MpegFile(new NonClosingStream(input)))
            {
                Convert(mpeg, output);
            }
        }

        private static void Convert(MpegFile mpeg, Stream output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!output.CanSeek) throw new ArgumentException("Output stream must be seekable.", nameof(output));

            int sampleRate = mpeg.SampleRate;
            int channels = mpeg.Channels;
            if (sampleRate <= 0 || channels <= 0)
                throw new InvalidDataException("Not a valid MP3 file: no audio stream found.");

            long headerStart = output.Position;
            var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

            int blockAlign = channels * 2; // 16-bit PCM
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(0u); // RIFF size, patched below
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
            writer.Write(0u); // data size, patched below

            var floats = new float[SamplesPerBlock];
            var pcm = new byte[SamplesPerBlock * 2];
            long dataBytes = 0;
            int read;
            while ((read = mpeg.ReadSamples(floats, 0, floats.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    float clamped = floats[i];
                    if (clamped > 1f) clamped = 1f;
                    else if (clamped < -1f) clamped = -1f;
                    short sample = (short)Math.Round(clamped * short.MaxValue);
                    pcm[i * 2] = (byte)sample;
                    pcm[i * 2 + 1] = (byte)(sample >> 8);
                }
                output.Write(pcm, 0, read * 2);
                dataBytes += read * 2;
            }

            if (dataBytes == 0)
                throw new InvalidDataException("Not a valid MP3 file: no audio frames could be decoded.");
            if (dataBytes + 36 > uint.MaxValue)
                throw new InvalidDataException("Decoded audio exceeds the 4 GB WAV limit.");

            // Sample count is always even in bytes (16-bit), so no pad byte is needed.
            output.Seek(headerStart + 4, SeekOrigin.Begin);
            writer.Write((uint)(36 + dataBytes)); // RIFF size
            output.Seek(headerStart + 40, SeekOrigin.Begin);
            writer.Write((uint)dataBytes);
            writer.Flush();
            output.Seek(0, SeekOrigin.End);
        }

        /// <summary>Shields a caller-owned stream from being disposed by MpegFile.</summary>
        private sealed class NonClosingStream : Stream
        {
            private readonly Stream _inner;
            public NonClosingStream(Stream inner) => _inner = inner;

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            protected override void Dispose(bool disposing) { /* leave the inner stream open */ }
        }
    }
}
