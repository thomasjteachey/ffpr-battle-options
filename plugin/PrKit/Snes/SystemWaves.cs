using System;

namespace PrKit.Snes
{
    // BRR: the S-DSP's 4-bit sample format, encoded (the decoder is the chip's, as SnesDsp has it).
    public static class BrrCodec
    {
        static int Clamp16(int v) => v > 32767 ? 32767 : v < -32768 ? -32768 : v;

        // one decoded sample (16-bit, as the chip stores it) from a nibble, a header's shift and filter
        // and the two previous decoded samples
        public static int Decode(int nibble, int shift, int filter, int p1, int p2)
        {
            int s = (nibble << shift) >> 1;
            if (shift >= 0xD) s = nibble < 0 ? -0x800 : 0;
            int q2 = p2 >> 1;
            if (filter >= 8)
            {
                s += p1; s -= q2;
                if (filter == 8) { s += q2 >> 4; s += (p1 * -3) >> 6; }
                else { s += (p1 * -13) >> 7; s += (q2 * 3) >> 4; }
            }
            else if (filter != 0) { s += p1 >> 1; s += (-p1) >> 5; }
            return (short)(Clamp16(s) * 2);
        }

        // PCM (a multiple of 16 samples) -> BRR blocks; the last block ends the sample, looping to
        // loopBlock (its first block uses no filter, so the loop joins cleanly), or not at all (-1).
        public static byte[] Encode(short[] pcm, int loopBlock = 0)
        {
            int blocks = pcm.Length / 16;
            var o = new byte[blocks * 9];
            int p1 = 0, p2 = 0;
            for (int b = 0; b < blocks; b++)
            {
                long bestErr = long.MaxValue; int bestF = 0, bestS = 0;
                byte[] bestN = null; int bp1 = 0, bp2 = 0;
                int maxFilter = (b == 0 || b == loopBlock) ? 0 : 3;
                for (int f = 0; f <= maxFilter; f++)
                    for (int sh = 0; sh <= 12; sh++)
                    {
                        long err = 0; int a1 = p1, a2 = p2;
                        var n = new byte[16];
                        for (int i = 0; i < 16; i++)
                        {
                            int target = pcm[b * 16 + i];
                            int best = 0; long be = long.MaxValue; int bd = 0;
                            for (int k = -8; k <= 7; k++)
                            {
                                int d = Decode(k, sh, f << 2, a1, a2);
                                long e = (long)(d - target) * (d - target);
                                if (e < be) { be = e; best = k; bd = d; }
                            }
                            n[i] = (byte)(best & 15);
                            err += be;
                            a2 = a1; a1 = bd;
                            if (err >= bestErr) break;
                        }
                        if (err < bestErr) { bestErr = err; bestF = f; bestS = sh; bestN = n; bp1 = a1; bp2 = a2; }
                    }
                o[b * 9] = (byte)((bestS << 4) | (bestF << 2) | (b == blocks - 1 ? 1 | (loopBlock >= 0 ? 2 : 0) : 0));
                for (int i = 0; i < 8; i++) o[b * 9 + 1 + i] = (byte)((bestN[i * 2] << 4) | bestN[i * 2 + 1]);
                p1 = bp1; p2 = bp2;
            }
            return o;
        }
    }

    // Stand-ins for the 8 short waveforms FF6's sound driver keeps resident (programs $00-$07: its sound
    // effects' waves, which some songs use for wind, rain, trains and textures). Those are the game's own
    // and aren't shipped; these are synthesized: each one's period, loudness and harmonic profile (its
    // fundamental and 8 bands of harmonics, measured once from the console's; random phases, no sample
    // data of the game's), a silent first block and a short lead-in as the originals have, and the
    // driver's pitch multiplier and envelope for them.
    public static class SystemWaves
    {
        // loop length (samples), RMS, lead-in blocks before the loop, fundamental (%), 8 harmonic bands (%)
        static readonly (int len, int rms, int lead, int f0, int[] bands)[] Shape =
        {
            (64, 15600, 0, 100, new[] { 31, 20, 23, 25, 26, 23, 33, 29 }),
            (64, 16400, 0, 100, new[] { 27, 25, 28, 19, 39, 51, 46, 21 }),
            (64, 16600, 0, 46, new[] { 23, 16, 27, 29, 42, 68, 37, 30 }),
            (64, 14300, 2, 100, new[] { 18, 1, 0, 0, 0, 0, 0, 0 }),
            (32, 19700, 2, 34, new[] { 20, 24, 13, 17, 11, 16, 25, 71 }),
            (32, 8400, 0, 100, new[] { 37, 19, 14, 11, 11, 9, 8, 8 }),
            (32, 13300, 0, 100, new[] { 49, 53, 58, 66, 48, 68, 89, 54 }),
            (96, 19600, 2, 9, new[] { 25, 26, 37, 25, 41, 67, 66, 60 }),
        };
        static readonly byte[] Tune0 = { 0xe1, 0xe1, 0xe1, 0xe1, 0x00, 0xe1, 0xe1, 0x00 };
        static readonly byte[] Tune1 = { 0xa0, 0xa0, 0xa0, 0xa0, 0x00, 0xa0, 0xa0, 0x00 };

        public static Ff6Instrument Make(int program)
        {
            var (n, rms, lead, f0, bands) = Shape[program & 7];
            var rng = new Random(0x5EED + program);
            var x = new double[n];
            int harmonics = n / 2;
            for (int h = 1; h <= harmonics; h++)
            {
                double mag = h == 1 ? f0 : bands[Math.Min(7, (h - 2) * 8 / Math.Max(1, harmonics - 1))];
                if (mag <= 0) continue;
                double ph = rng.NextDouble() * 2 * Math.PI;
                for (int k = 0; k < n; k++) x[k] += mag * Math.Cos(2 * Math.PI * h * k / n + ph);
            }
            double sum = 0;
            foreach (var v in x) sum += v * v;
            double scale = rms / Math.Max(1e-9, Math.Sqrt(sum / n));
            int loopStart = (1 + lead) * 16;
            var pcm = new short[loopStart + n];
            for (int k = 16; k < pcm.Length; k++)
                pcm[k] = (short)Math.Max(-32000, Math.Min(32000, x[(k - 16) % n] * scale));
            var brr = BrrCodec.Encode(pcm, loopStart / 16);
            return new Ff6Instrument
            {
                Sample = new BrrSample(brr, loopStart / 16 * 9, $"stand-in {program}"),
                Tune0 = Tune0[program & 7], Tune1 = Tune1[program & 7], Adsr1 = 0xff, Adsr2 = 0xe0,
            };
        }
    }
}
