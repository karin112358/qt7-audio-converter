using System;

namespace Qt7AudioConverter
{
    /// <summary>
    /// Streaming sample-rate converter using Catmull-Rom cubic interpolation
    /// over interleaved frames. Keeps a few frames of history between blocks,
    /// so arbitrarily long streams convert in constant memory.
    /// </summary>
    internal sealed class CatmullRomResampler
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
}
