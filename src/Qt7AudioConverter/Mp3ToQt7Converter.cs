using System;
using System.IO;
using NLayer;

namespace Qt7AudioConverter
{
    /// <summary>What an MP3 conversion did: source format vs. written format.</summary>
    public readonly struct Mp3ConversionInfo
    {
        public Mp3ConversionInfo(int sourceSampleRate, int sourceChannels, double sourceDurationSeconds,
            int outputSampleRate, int outputChannels, double outputDurationSeconds, long clippedSamples)
        {
            SourceSampleRate = sourceSampleRate;
            SourceChannels = sourceChannels;
            SourceDurationSeconds = sourceDurationSeconds;
            OutputSampleRate = outputSampleRate;
            OutputChannels = outputChannels;
            OutputDurationSeconds = outputDurationSeconds;
            ClippedSamples = clippedSamples;
        }

        public int SourceSampleRate { get; }
        public int SourceChannels { get; }
        public double SourceDurationSeconds { get; }
        public int OutputSampleRate { get; }
        public int OutputChannels { get; }
        public double OutputDurationSeconds { get; }

        /// <summary>Samples that exceeded full scale and were clamped (e.g. due
        /// to a volume boost).</summary>
        public long ClippedSamples { get; }
    }

    /// <summary>
    /// Decodes an MP3 file to a canonical QuickTime 7-style WAV
    /// (RIFF → fmt → data, 16-bit PCM, always 44100 Hz — resampled if the MP3
    /// uses another rate, since legacy devices commonly accept only 44.1 kHz).
    /// Uses the managed NLayer decoder, so it works on Windows and macOS
    /// without any native dependencies.
    /// </summary>
    public static class Mp3ToQt7Converter
    {
        /// <summary>Output sample rate of every conversion.</summary>
        public const int TargetSampleRate = CanonicalWavWriter.TargetSampleRate;

        private const int SamplesPerBlock = 16 * 1024;

        /// <summary>
        /// Decodes <paramref name="inputPath"/> and writes a canonical 16-bit
        /// PCM WAV at 44100 Hz to <paramref name="outputPath"/> (overwriting it
        /// if it exists). With <paramref name="downmixToMono"/> the channels
        /// are averaged into one.
        /// </summary>
        public static Mp3ConversionInfo Convert(string inputPath, string outputPath, bool downmixToMono = false, float volume = 1f)
        {
            using (var mpeg = new MpegFile(inputPath))
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                return Convert(mpeg, output, downmixToMono, volume);
            }
        }

        /// <summary>
        /// Decodes the MP3 in <paramref name="input"/> and writes a canonical
        /// 16-bit PCM WAV at 44100 Hz to <paramref name="output"/>. The output
        /// stream must be seekable (the size fields are patched after
        /// decoding); both streams are left open.
        /// </summary>
        public static Mp3ConversionInfo Convert(Stream input, Stream output, bool downmixToMono = false, float volume = 1f)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using (var mpeg = new MpegFile(new NonClosingStream(input)))
            {
                return Convert(mpeg, output, downmixToMono, volume);
            }
        }

        private static Mp3ConversionInfo Convert(MpegFile mpeg, Stream output, bool downmixToMono, float volume)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!output.CanSeek) throw new ArgumentException("Output stream must be seekable.", nameof(output));
            if (!(volume > 0f)) throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be greater than 0.");

            int srcRate = mpeg.SampleRate;
            int srcChannels = mpeg.Channels;
            if (srcRate <= 0 || srcChannels <= 0)
                throw new InvalidDataException("Not a valid MP3 file: no audio stream found.");

            int outChannels = downmixToMono ? 1 : srcChannels;
            var resampler = srcRate != TargetSampleRate
                ? new CatmullRomResampler(outChannels, srcRate, TargetSampleRate)
                : null;

            long headerStart = CanonicalWavWriter.WriteHeader(output, TargetSampleRate, outChannels);

            var decoded = new float[SamplesPerBlock];
            var frames = new float[SamplesPerBlock];      // downmixed / aligned frames
            var resampled = new float[SamplesPerBlock * 2];
            var pcm = new byte[SamplesPerBlock * 4];
            long dataBytes = 0;
            long clipped = 0;
            long srcFrames = 0;
            int carry = 0; // decoded samples not forming a whole frame yet
            int read;
            while ((read = mpeg.ReadSamples(decoded, carry, decoded.Length - carry)) > 0)
            {
                int total = carry + read;
                int frameCount = total / srcChannels;
                carry = total - frameCount * srcChannels;
                srcFrames += frameCount;

                if (downmixToMono && srcChannels > 1)
                {
                    for (int f = 0; f < frameCount; f++)
                    {
                        float sum = 0;
                        for (int c = 0; c < srcChannels; c++) sum += decoded[f * srcChannels + c];
                        frames[f] = sum / srcChannels;
                    }
                }
                else
                {
                    Array.Copy(decoded, frames, frameCount * srcChannels);
                }

                // Keep the partial frame for the next block.
                for (int i = 0; i < carry; i++)
                    decoded[i] = decoded[frameCount * srcChannels + i];

                if (volume != 1f)
                {
                    for (int i = 0; i < frameCount * outChannels; i++)
                        frames[i] *= volume;
                }

                int outSamples;
                float[] outBuf;
                if (resampler != null)
                {
                    outSamples = resampler.Process(frames, frameCount, ref resampled);
                    outBuf = resampled;
                }
                else
                {
                    outSamples = frameCount * outChannels;
                    outBuf = frames;
                }
                dataBytes += CanonicalWavWriter.WritePcm16(output, outBuf, outSamples, ref pcm, ref clipped);
            }

            if (resampler != null)
            {
                int tail = resampler.Flush(ref resampled);
                dataBytes += CanonicalWavWriter.WritePcm16(output, resampled, tail, ref pcm, ref clipped);
            }

            if (dataBytes == 0)
                throw new InvalidDataException("Not a valid MP3 file: no audio frames could be decoded.");
            CanonicalWavWriter.PatchSizes(output, headerStart, dataBytes);

            return new Mp3ConversionInfo(
                srcRate, srcChannels, srcFrames / (double)srcRate,
                TargetSampleRate, outChannels, dataBytes / 2.0 / outChannels / TargetSampleRate,
                clipped);
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
