using System;
using System.IO;
using System.Text;
using NLayer;

namespace Qt7AudioConverter
{
    /// <summary>What an MP3 conversion did: source format vs. written format.</summary>
    public readonly struct Mp3ConversionInfo
    {
        public Mp3ConversionInfo(int sourceSampleRate, int sourceChannels, int outputSampleRate, int outputChannels)
        {
            SourceSampleRate = sourceSampleRate;
            SourceChannels = sourceChannels;
            OutputSampleRate = outputSampleRate;
            OutputChannels = outputChannels;
        }

        public int SourceSampleRate { get; }
        public int SourceChannels { get; }
        public int OutputSampleRate { get; }
        public int OutputChannels { get; }
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
        public const int TargetSampleRate = 44100;

        private const int SamplesPerBlock = 16 * 1024;

        /// <summary>
        /// Decodes <paramref name="inputPath"/> and writes a canonical 16-bit
        /// PCM WAV at 44100 Hz to <paramref name="outputPath"/> (overwriting it
        /// if it exists). With <paramref name="downmixToMono"/> the channels
        /// are averaged into one.
        /// </summary>
        public static Mp3ConversionInfo Convert(string inputPath, string outputPath, bool downmixToMono = false)
        {
            using (var mpeg = new MpegFile(inputPath))
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                return Convert(mpeg, output, downmixToMono);
            }
        }

        /// <summary>
        /// Decodes the MP3 in <paramref name="input"/> and writes a canonical
        /// 16-bit PCM WAV at 44100 Hz to <paramref name="output"/>. The output
        /// stream must be seekable (the size fields are patched after
        /// decoding); both streams are left open.
        /// </summary>
        public static Mp3ConversionInfo Convert(Stream input, Stream output, bool downmixToMono = false)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using (var mpeg = new MpegFile(new NonClosingStream(input)))
            {
                return Convert(mpeg, output, downmixToMono);
            }
        }

        private static Mp3ConversionInfo Convert(MpegFile mpeg, Stream output, bool downmixToMono)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!output.CanSeek) throw new ArgumentException("Output stream must be seekable.", nameof(output));

            int srcRate = mpeg.SampleRate;
            int srcChannels = mpeg.Channels;
            if (srcRate <= 0 || srcChannels <= 0)
                throw new InvalidDataException("Not a valid MP3 file: no audio stream found.");

            int outChannels = downmixToMono ? 1 : srcChannels;
            var resampler = srcRate != TargetSampleRate
                ? new CatmullRomResampler(outChannels, srcRate, TargetSampleRate)
                : null;

            long headerStart = output.Position;
            var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

            int blockAlign = outChannels * 2; // 16-bit PCM
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(0u); // RIFF size, patched below
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16u);
            writer.Write((ushort)1); // PCM
            writer.Write((ushort)outChannels);
            writer.Write((uint)TargetSampleRate);
            writer.Write((uint)(TargetSampleRate * blockAlign)); // byte rate
            writer.Write((ushort)blockAlign);
            writer.Write((ushort)16); // bits per sample
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(0u); // data size, patched below

            var decoded = new float[SamplesPerBlock];
            var frames = new float[SamplesPerBlock];      // downmixed / aligned frames
            var resampled = new float[SamplesPerBlock * 2];
            var pcm = new byte[Math.Max(SamplesPerBlock, resampled.Length) * 2];
            long dataBytes = 0;
            int carry = 0; // decoded samples not forming a whole frame yet
            int read;
            while ((read = mpeg.ReadSamples(decoded, carry, decoded.Length - carry)) > 0)
            {
                int total = carry + read;
                int frameCount = total / srcChannels;
                carry = total - frameCount * srcChannels;

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
                dataBytes += WritePcm(output, outBuf, outSamples, ref pcm);
            }

            if (resampler != null)
            {
                int tail = resampler.Flush(ref resampled);
                dataBytes += WritePcm(output, resampled, tail, ref pcm);
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

            return new Mp3ConversionInfo(srcRate, srcChannels, TargetSampleRate, outChannels);
        }

        private static long WritePcm(Stream output, float[] samples, int count, ref byte[] pcm)
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

        /// <summary>
        /// Streaming sample-rate converter using Catmull-Rom cubic
        /// interpolation over interleaved frames. Keeps a few frames of
        /// history between blocks, so arbitrarily long streams convert in
        /// constant memory.
        /// </summary>
        private sealed class CatmullRomResampler
        {
            private readonly int _ch;
            private readonly double _step; // source frames advanced per output frame
            private double _pos;           // absolute source-frame position of the next output frame
            private float[] _buf;          // interleaved frames buffered for interpolation
            private int _frames;           // frames currently in _buf
            private long _start;           // absolute source-frame index of _buf frame 0

            public CatmullRomResampler(int channels, int srcRate, int dstRate)
            {
                _ch = channels;
                _step = (double)srcRate / dstRate;
                _buf = new float[8192 * channels];
            }

            /// <summary>Feeds <paramref name="frameCount"/> interleaved frames; appends
            /// resampled interleaved samples to <paramref name="output"/> (grown as
            /// needed) and returns how many samples were produced.</summary>
            public int Process(float[] input, int frameCount, ref float[] output)
            {
                if (frameCount == 0) return 0;
                if ((_frames + frameCount) * _ch > _buf.Length)
                    Array.Resize(ref _buf, (_frames + frameCount) * _ch * 2);
                Array.Copy(input, 0, _buf, _frames * _ch, frameCount * _ch);
                _frames += frameCount;
                return Produce(ref output);
            }

            /// <summary>Call once at end of stream: pads with the final frame so the
            /// interpolation can consume through the last real frame.</summary>
            public int Flush(ref float[] output)
            {
                if (_frames == 0) return 0;
                if ((_frames + 2) * _ch > _buf.Length)
                    Array.Resize(ref _buf, (_frames + 2) * _ch);
                for (int p = 0; p < 2; p++)
                    Array.Copy(_buf, (_frames - 1) * _ch, _buf, (_frames + p) * _ch, _ch);
                _frames += 2;
                return Produce(ref output);
            }

            private int Produce(ref float[] output)
            {
                int produced = 0;
                long last = _start + _frames - 1;
                while ((long)Math.Floor(_pos) + 2 <= last)
                {
                    long i = (long)Math.Floor(_pos);
                    float t = (float)(_pos - i);
                    int i1 = (int)(i - _start);
                    int i0 = Math.Max(i1 - 1, 0); // clamp at the very start of the stream
                    if ((produced + _ch) > output.Length)
                        Array.Resize(ref output, Math.Max(output.Length * 2, produced + _ch));
                    for (int c = 0; c < _ch; c++)
                    {
                        float p0 = _buf[i0 * _ch + c];
                        float p1 = _buf[i1 * _ch + c];
                        float p2 = _buf[(i1 + 1) * _ch + c];
                        float p3 = _buf[(i1 + 2) * _ch + c];
                        float t2 = t * t, t3 = t2 * t;
                        output[produced + c] = 0.5f * (2f * p1
                            + (-p0 + p2) * t
                            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                            + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
                    }
                    produced += _ch;
                    _pos += _step;
                }

                // Drop frames the interpolation no longer needs (keep one behind).
                long keepFrom = (long)Math.Floor(_pos) - 1;
                if (keepFrom > _start)
                {
                    if (keepFrom > _start + _frames) keepFrom = _start + _frames;
                    int dropFrames = (int)(keepFrom - _start);
                    Array.Copy(_buf, dropFrames * _ch, _buf, 0, (_frames - dropFrames) * _ch);
                    _frames -= dropFrames;
                    _start = keepFrom;
                }
                return produced;
            }
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
