using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QuickTimeWavConverter
{
    /// <summary>
    /// Rewrites WAV files saved by QuickTime 10 (which inserts JUNK/FLLR padding
    /// chunks) into the canonical layout produced by QuickTime 7:
    /// RIFF → fmt → [fact] → data. The audio payload is copied byte-for-byte,
    /// so the conversion is lossless.
    /// </summary>
    public static class WavQt7Converter
    {
        private const int CopyBufferSize = 64 * 1024;

        /// <summary>
        /// Converts <paramref name="inputPath"/> and writes the canonical WAV to
        /// <paramref name="outputPath"/> (overwriting it if it exists).
        /// </summary>
        public static void Convert(string inputPath, string outputPath)
        {
            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Convert(input, output);
            }
        }

        /// <summary>
        /// Reads a RIFF/WAVE stream from <paramref name="input"/> and writes the
        /// canonical form to <paramref name="output"/>. The input stream must be
        /// seekable; both streams are left open.
        /// </summary>
        public static void Convert(Stream input, Stream output)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!input.CanSeek) throw new ArgumentException("Input stream must be seekable.", nameof(input));

            var chunks = ReadChunkTable(input);

            if (!chunks.TryGetValue("fmt ", out var fmtChunk))
                throw new InvalidDataException("Not a valid WAV file: missing 'fmt ' chunk.");
            if (!chunks.TryGetValue("data", out var dataChunk))
                throw new InvalidDataException("Not a valid WAV file: missing 'data' chunk.");
            chunks.TryGetValue("fact", out var factChunk);

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
