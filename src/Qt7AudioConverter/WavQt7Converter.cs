using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Qt7AudioConverter
{
    /// <summary>What a WAV conversion did: source format, written format, and
    /// whether the audio was copied losslessly.</summary>
    public readonly struct WavConversionInfo
    {
        public WavConversionInfo(
            int sourceSampleRate, int sourceBitsPerSample, int sourceChannels,
            int outputSampleRate, int outputBitsPerSample, int outputChannels,
            bool lossless)
        {
            SourceSampleRate = sourceSampleRate;
            SourceBitsPerSample = sourceBitsPerSample;
            SourceChannels = sourceChannels;
            OutputSampleRate = outputSampleRate;
            OutputBitsPerSample = outputBitsPerSample;
            OutputChannels = outputChannels;
            Lossless = lossless;
        }

        public int SourceSampleRate { get; }
        public int SourceBitsPerSample { get; }
        public int SourceChannels { get; }
        public int OutputSampleRate { get; }
        public int OutputBitsPerSample { get; }
        public int OutputChannels { get; }

        /// <summary>True when the audio bytes were copied unchanged (only the
        /// chunk layout was rewritten).</summary>
        public bool Lossless { get; }
    }

    /// <summary>
    /// Rewrites WAV files into the canonical layout produced by QuickTime 7
    /// (RIFF → fmt → data), dropping the JUNK/FLLR padding chunks modern
    /// software inserts. Files that are already 44.1 kHz with 8- or 16-bit
    /// integer PCM are copied losslessly; anything else (48/96 kHz, 24/32-bit,
    /// float) is decoded and written as 44.1 kHz 16-bit PCM, since legacy
    /// devices commonly accept nothing else.
    /// </summary>
    public static class WavQt7Converter
    {
        private const int TargetSampleRate = CanonicalWavWriter.TargetSampleRate;
        private const int CopyBufferSize = 64 * 1024;
        private const int FramesPerBlock = 16 * 1024;

        /// <summary>
        /// Converts <paramref name="inputPath"/> and writes the canonical WAV to
        /// <paramref name="outputPath"/> (overwriting it if it exists). With
        /// <paramref name="downmixToMono"/> multi-channel audio is averaged
        /// into one channel (which forces the decode path).
        /// </summary>
        public static WavConversionInfo Convert(string inputPath, string outputPath, bool downmixToMono = false)
        {
            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                return Convert(input, output, downmixToMono);
            }
        }

        /// <summary>
        /// Reads a RIFF/WAVE stream from <paramref name="input"/> and writes the
        /// canonical form to <paramref name="output"/>. Both streams must be
        /// seekable; both are left open.
        /// </summary>
        public static WavConversionInfo Convert(Stream input, Stream output, bool downmixToMono = false)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!input.CanSeek) throw new ArgumentException("Input stream must be seekable.", nameof(input));
            if (!output.CanSeek) throw new ArgumentException("Output stream must be seekable.", nameof(output));

            var chunks = ReadChunkTable(input);

            if (!chunks.TryGetValue("fmt ", out var fmtChunk))
                throw new InvalidDataException("Not a valid WAV file: missing 'fmt ' chunk.");
            if (!chunks.TryGetValue("data", out var dataChunk))
                throw new InvalidDataException("Not a valid WAV file: missing 'data' chunk.");
            chunks.TryGetValue("fact", out var factChunk);

            var fmt = ParseFormat(input, fmtChunk);

            bool compliant = fmt.IsIntegerPcm
                && fmt.SampleRate == TargetSampleRate
                && (fmt.BitsPerSample == 8 || fmt.BitsPerSample == 16);
            if (compliant && !(downmixToMono && fmt.Channels > 1))
            {
                CopyLossless(input, output, fmtChunk, factChunk, dataChunk);
                return new WavConversionInfo(
                    fmt.SampleRate, fmt.BitsPerSample, fmt.Channels,
                    fmt.SampleRate, fmt.BitsPerSample, fmt.Channels,
                    lossless: true);
            }

            int outChannels = Decode(input, output, fmt, dataChunk, downmixToMono);
            return new WavConversionInfo(
                fmt.SampleRate, fmt.BitsPerSample, fmt.Channels,
                TargetSampleRate, 16, outChannels,
                lossless: false);
        }

        // ---------- format parsing ----------

        private readonly struct FormatInfo
        {
            public FormatInfo(bool isIntegerPcm, bool isFloat, int channels, int sampleRate, int bitsPerSample)
            {
                IsIntegerPcm = isIntegerPcm;
                IsFloat = isFloat;
                Channels = channels;
                SampleRate = sampleRate;
                BitsPerSample = bitsPerSample;
            }

            public bool IsIntegerPcm { get; }
            public bool IsFloat { get; }
            public int Channels { get; }
            public int SampleRate { get; }
            public int BitsPerSample { get; }
        }

        private static FormatInfo ParseFormat(Stream input, ChunkInfo fmtChunk)
        {
            if (fmtChunk.Size < 16)
                throw new InvalidDataException("Not a valid WAV file: 'fmt ' chunk is too small.");

            var payload = new byte[Math.Min(fmtChunk.Size, 40)];
            input.Seek(fmtChunk.DataOffset, SeekOrigin.Begin);
            ReadExactly(input, payload, payload.Length);

            int tag = payload[0] | payload[1] << 8;
            int channels = payload[2] | payload[3] << 8;
            int rate = payload[4] | payload[5] << 8 | payload[6] << 16 | payload[7] << 24;
            int bits = payload[14] | payload[15] << 8;

            if (tag == 0xFFFE && payload.Length >= 26) // WAVE_FORMAT_EXTENSIBLE: real tag leads the SubFormat GUID
                tag = payload[24] | payload[25] << 8;

            if (channels <= 0 || rate <= 0 || bits <= 0)
                throw new InvalidDataException("Not a valid WAV file: malformed 'fmt ' chunk.");

            return new FormatInfo(tag == 1, tag == 3, channels, rate, bits);
        }

        // ---------- lossless path ----------

        private static void CopyLossless(Stream input, Stream output, ChunkInfo fmtChunk, ChunkInfo? factChunk, ChunkInfo dataChunk)
        {
            long riffSize = 4; // "WAVE"
            riffSize += 8 + fmtChunk.PaddedSize;
            if (factChunk != null) riffSize += 8 + factChunk.PaddedSize;
            riffSize += 8 + dataChunk.PaddedSize;
            if (riffSize > uint.MaxValue)
                throw new InvalidDataException("Resulting RIFF size exceeds the 4 GB WAV limit.");

            var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((uint)riffSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));

            WriteChunk(input, writer, fmtChunk);
            if (factChunk != null) WriteChunk(input, writer, factChunk);
            WriteChunk(input, writer, dataChunk);
            writer.Flush();
        }

        // ---------- decode/resample path ----------

        private static int Decode(Stream input, Stream output, FormatInfo fmt, ChunkInfo dataChunk, bool downmixToMono)
        {
            int bytesPerSample = fmt.BitsPerSample / 8;
            bool supported = fmt.IsIntegerPcm
                ? fmt.BitsPerSample is 8 or 16 or 24 or 32
                : fmt.IsFloat && fmt.BitsPerSample is 32 or 64;
            if (!supported)
                throw new InvalidDataException(
                    $"Unsupported WAV format: {(fmt.IsIntegerPcm ? "integer" : fmt.IsFloat ? "float" : "compressed")} {fmt.BitsPerSample}-bit. " +
                    "Supported are 8/16/24/32-bit PCM and 32/64-bit float.");

            int frameBytes = bytesPerSample * fmt.Channels;
            int outChannels = downmixToMono ? 1 : fmt.Channels;
            var resampler = fmt.SampleRate != TargetSampleRate
                ? new CatmullRomResampler(outChannels, fmt.SampleRate, TargetSampleRate)
                : null;

            long headerStart = CanonicalWavWriter.WriteHeader(output, TargetSampleRate, outChannels);

            var raw = new byte[FramesPerBlock * frameBytes];
            var frames = new float[FramesPerBlock * fmt.Channels];
            var resampled = new float[FramesPerBlock * fmt.Channels * 2];
            var pcm = new byte[FramesPerBlock * fmt.Channels * 2];
            long remaining = dataChunk.Size - dataChunk.Size % frameBytes; // ignore a trailing partial frame
            long dataBytes = 0;

            input.Seek(dataChunk.DataOffset, SeekOrigin.Begin);
            while (remaining > 0)
            {
                int want = (int)Math.Min(raw.Length, remaining);
                ReadExactly(input, raw, want);
                remaining -= want;
                int frameCount = want / frameBytes;

                DecodeSamples(raw, frames, frameCount * fmt.Channels, bytesPerSample, fmt.IsFloat);

                if (downmixToMono && fmt.Channels > 1)
                {
                    for (int f = 0; f < frameCount; f++)
                    {
                        float sum = 0;
                        for (int c = 0; c < fmt.Channels; c++) sum += frames[f * fmt.Channels + c];
                        frames[f] = sum / fmt.Channels;
                    }
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
                dataBytes += CanonicalWavWriter.WritePcm16(output, outBuf, outSamples, ref pcm);
            }

            if (resampler != null)
            {
                int tail = resampler.Flush(ref resampled);
                dataBytes += CanonicalWavWriter.WritePcm16(output, resampled, tail, ref pcm);
            }

            CanonicalWavWriter.PatchSizes(output, headerStart, dataBytes);
            return outChannels;
        }

        private static void DecodeSamples(byte[] raw, float[] samples, int count, int bytesPerSample, bool isFloat)
        {
            if (isFloat && bytesPerSample == 4)
            {
                for (int i = 0; i < count; i++)
                    samples[i] = BitConverter.ToSingle(raw, i * 4);
            }
            else if (isFloat) // 64-bit float
            {
                for (int i = 0; i < count; i++)
                    samples[i] = (float)BitConverter.ToDouble(raw, i * 8);
            }
            else switch (bytesPerSample)
            {
                case 1: // 8-bit PCM is unsigned
                    for (int i = 0; i < count; i++)
                        samples[i] = (raw[i] - 128) / 128f;
                    break;
                case 2:
                    for (int i = 0; i < count; i++)
                        samples[i] = (short)(raw[i * 2] | raw[i * 2 + 1] << 8) / 32768f;
                    break;
                case 3:
                    for (int i = 0; i < count; i++)
                    {
                        int v = raw[i * 3] | raw[i * 3 + 1] << 8 | (sbyte)raw[i * 3 + 2] << 16;
                        samples[i] = v / 8388608f;
                    }
                    break;
                default: // 4: 32-bit int
                    for (int i = 0; i < count; i++)
                        samples[i] = BitConverter.ToInt32(raw, i * 4) / 2147483648f;
                    break;
            }
        }

        private static void ReadExactly(Stream input, byte[] buffer, int count)
        {
            int done = 0;
            while (done < count)
            {
                int read = input.Read(buffer, done, count - done);
                if (read <= 0)
                    throw new InvalidDataException("Unexpected end of file while reading audio data.");
                done += read;
            }
        }

        // ---------- chunk plumbing ----------

        private sealed class ChunkInfo
        {
            public ChunkInfo(string id, long dataOffset, uint size)
            {
                Id = id;
                DataOffset = dataOffset;
                Size = size;
            }

            public string Id { get; }
            public long DataOffset { get; }
            public uint Size { get; }
            public long PaddedSize => Size + (Size & 1);
        }

        private static Dictionary<string, ChunkInfo> ReadChunkTable(Stream input)
        {
            var reader = new BinaryReader(input, Encoding.ASCII, leaveOpen: true);
            input.Seek(0, SeekOrigin.Begin);

            if (ReadFourCc(reader) != "RIFF")
                throw new InvalidDataException("Not a RIFF file: missing 'RIFF' signature.");
            reader.ReadUInt32(); // declared RIFF size; recomputed on write, so ignored
            if (ReadFourCc(reader) != "WAVE")
                throw new InvalidDataException("Not a WAV file: missing 'WAVE' form type.");

            var chunks = new Dictionary<string, ChunkInfo>(StringComparer.Ordinal);
            while (input.Position + 8 <= input.Length)
            {
                string id = ReadFourCc(reader);
                uint size = reader.ReadUInt32();
                long dataOffset = input.Position;

                // A duplicate chunk id would be malformed; keep the first occurrence.
                if (!chunks.ContainsKey(id))
                    chunks[id] = new ChunkInfo(id, dataOffset, size);

                long next = dataOffset + size + (size & 1);
                if (next > input.Length)
                {
                    // Truncated final chunk: clamp so the readable part is still converted.
                    if (id == "data" && chunks[id].DataOffset == dataOffset)
                        chunks[id] = new ChunkInfo(id, dataOffset, (uint)(input.Length - dataOffset));
                    break;
                }
                input.Seek(next, SeekOrigin.Begin);
            }
            return chunks;
        }

        private static string ReadFourCc(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(4);
            if (bytes.Length != 4)
                throw new InvalidDataException("Unexpected end of file while reading chunk header.");
            return Encoding.ASCII.GetString(bytes);
        }

        private static void WriteChunk(Stream input, BinaryWriter writer, ChunkInfo chunk)
        {
            writer.Write(Encoding.ASCII.GetBytes(chunk.Id));
            writer.Write(chunk.Size);
            writer.Flush();

            input.Seek(chunk.DataOffset, SeekOrigin.Begin);
            var buffer = new byte[CopyBufferSize];
            long remaining = chunk.Size;
            while (remaining > 0)
            {
                int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                    throw new InvalidDataException($"Unexpected end of file while copying '{chunk.Id}' chunk.");
                writer.BaseStream.Write(buffer, 0, read);
                remaining -= read;
            }
            if ((chunk.Size & 1) != 0)
                writer.BaseStream.WriteByte(0); // RIFF chunks are word-aligned
        }
    }
}
