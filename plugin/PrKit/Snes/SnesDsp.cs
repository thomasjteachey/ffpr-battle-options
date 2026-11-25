using System;

namespace PrKit.Snes
{
    // A BRR sample as the S-DSP's sample directory sees it: 9-byte blocks (header + 8 bytes of
    // 4-bit samples) and the byte offset blocks continue from after a block with the end flag.
    public sealed class BrrSample
    {
        public readonly byte[] Data;       // whole 9-byte blocks
        public readonly int Loop;          // byte offset of the loop block (a multiple of 9)
        public readonly string Name;

        public BrrSample(byte[] data, int loop, string name = null)
        {
            int blocks = Math.Max(1, data.Length / 9);
            if (data.Length < 9 || data.Length % 9 != 0)
            {
                var d = new byte[blocks * 9];
                Array.Copy(data, d, Math.Min(data.Length, d.Length));
                if (data.Length < 9) d[0] = 1;   // a lone end block: silence
                data = d;
            }
            Data = data;
            Loop = (loop >= 0 && loop < data.Length) ? loop - loop % 9 : 0;
            Name = name;
        }
    }

    // The SNES S-DSP (Sony's sound DSP), one 32 kHz stereo sample at a time: 8 voices of BRR
    // decoding with Gaussian interpolation, pitch (with pitch modulation), ADSR/GAIN envelopes,
    // noise, the 8-tap FIR echo with feedback, main/echo volumes, KON/KOFF and FLG.
    //
    // Written from the chip's public documentation and the behaviour open-source SNES emulators
    // agree on (blargg's SPC_DSP notes, anomie's and nocash's documents); sample-accurate rather
    // than clock-accurate: register writes take effect at the next sample, KON/KOFF are polled on
    // every other sample as the chip does. Samples come from a table of BrrSample (SRCN -> sample)
    // instead of 64 KB of APU RAM, and the echo buffer is its own array (EDL sets its length).
    public sealed class SnesDsp
    {
        public const int SampleRate = 32000;

        // global registers
        public const int MVOLL = 0x0C, MVOLR = 0x1C, EVOLL = 0x2C, EVOLR = 0x3C, KON = 0x4C, KOF = 0x5C,
            FLG = 0x6C, ENDX = 0x7C, EFB = 0x0D, PMON = 0x2D, NON = 0x3D, EON = 0x4D, DIR = 0x5D,
            ESA = 0x6D, EDL = 0x7D, FIR = 0x0F;
        // voice registers (+ voice * 0x10)
        public const int VOLL = 0, VOLR = 1, PITCHL = 2, PITCHH = 3, SRCN = 4, ADSR1 = 5, ADSR2 = 6, GAIN = 7;

        // the S-DSP's interpolation table (its ROM; the same 512 values every emulator uses)
        static readonly short[] Gauss = {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2,
            2, 2, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 5, 5, 5, 5,
            6, 6, 6, 6, 7, 7, 7, 8, 8, 8, 9, 9, 9, 10, 10, 10,
            11, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 15, 16, 16, 17, 17,
            18, 19, 19, 20, 20, 21, 21, 22, 23, 23, 24, 24, 25, 26, 27, 27,
            28, 29, 29, 30, 31, 32, 32, 33, 34, 35, 36, 36, 37, 38, 39, 40,
            41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56,
            58, 59, 60, 61, 62, 64, 65, 66, 67, 69, 70, 71, 73, 74, 76, 77,
            78, 80, 81, 83, 84, 86, 87, 89, 90, 92, 94, 95, 97, 99, 100, 102,
            104, 106, 107, 109, 111, 113, 115, 117, 118, 120, 122, 124, 126, 128, 130, 132,
            134, 137, 139, 141, 143, 145, 147, 150, 152, 154, 156, 159, 161, 163, 166, 168,
            171, 173, 175, 178, 180, 183, 186, 188, 191, 193, 196, 199, 201, 204, 207, 210,
            212, 215, 218, 221, 224, 227, 230, 233, 236, 239, 242, 245, 248, 251, 254, 257,
            260, 263, 267, 270, 273, 276, 280, 283, 286, 290, 293, 297, 300, 304, 307, 311,
            314, 318, 321, 325, 328, 332, 336, 339, 343, 347, 351, 354, 358, 362, 366, 370,
            374, 378, 381, 385, 389, 393, 397, 401, 405, 410, 414, 418, 422, 426, 430, 434,
            439, 443, 447, 451, 456, 460, 464, 469, 473, 477, 482, 486, 491, 495, 499, 504,
            508, 513, 517, 522, 527, 531, 536, 540, 545, 550, 554, 559, 563, 568, 573, 577,
            582, 587, 592, 596, 601, 606, 611, 615, 620, 625, 630, 635, 640, 644, 649, 654,
            659, 664, 669, 674, 678, 683, 688, 693, 698, 703, 708, 713, 718, 723, 728, 732,
            737, 742, 747, 752, 757, 762, 767, 772, 777, 782, 787, 792, 797, 802, 806, 811,
            816, 821, 826, 831, 836, 841, 846, 851, 855, 860, 865, 870, 875, 880, 884, 889,
            894, 899, 904, 908, 913, 918, 923, 927, 932, 937, 941, 946, 951, 955, 960, 965,
            969, 974, 978, 983, 988, 992, 997, 1001, 1005, 1010, 1014, 1019, 1023, 1027, 1032, 1036,
            1040, 1045, 1049, 1053, 1057, 1061, 1066, 1070, 1074, 1078, 1082, 1086, 1090, 1094, 1098, 1102,
            1106, 1109, 1113, 1117, 1121, 1125, 1128, 1132, 1136, 1139, 1143, 1146, 1150, 1153, 1157, 1160,
            1164, 1167, 1170, 1174, 1177, 1180, 1183, 1186, 1190, 1193, 1196, 1199, 1202, 1205, 1207, 1210,
            1213, 1216, 1219, 1221, 1224, 1227, 1229, 1232, 1234, 1237, 1239, 1241, 1244, 1246, 1248, 1251,
            1253, 1255, 1257, 1259, 1261, 1263, 1265, 1267, 1269, 1270, 1272, 1274, 1275, 1277, 1279, 1280,
            1282, 1283, 1284, 1286, 1287, 1288, 1290, 1291, 1292, 1293, 1294, 1295, 1296, 1297, 1297, 1298,
            1299, 1300, 1300, 1301, 1302, 1302, 1303, 1303, 1303, 1304, 1304, 1304, 1304, 1304, 1305, 1305,
        };

        // envelope/noise rate counters: how many samples between steps for each 5-bit rate, and
        // the phase offsets that make rates of one family step on different samples
        static readonly int[] CounterRates = {
            0x7800 + 1, 2048, 1536, 1280, 1024, 768, 640, 512, 384, 320, 256, 192, 160, 128, 96, 80,
            64, 48, 40, 32, 24, 20, 16, 12, 10, 8, 6, 5, 4, 3, 2, 1 };
        static readonly int[] CounterOffsets = {
            1, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536,
            0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 0, 0 };
        const int CounterRange = 2048 * 5 * 3;

        enum EnvMode { Release, Attack, Decay, Sustain }

        sealed class Voice
        {
            public readonly int[] Buf = new int[24];   // 12 decoded samples, doubled for wrap-around
            public int BufPos;
            public int InterpPos;
            public BrrSample Sample;
            public int BlockOff;                       // byte offset of the current block in Sample.Data
            public int BrrOff = 1;                     // 1, 3, 5, 7: next byte pair in the block
            public int KonDelay;
            public EnvMode Mode;
            public int Env, HiddenEnv;
            public bool Silent = true;                 // released to zero: nothing to render until KON
        }

        readonly byte[] regs = new byte[128];
        readonly Voice[] voices = new Voice[8];
        public readonly BrrSample[] Sources = new BrrSample[256];   // SRCN -> sample

        int counter;
        int noise = 0x4000;
        bool everyOther;
        int konLatched, newKon;
        int koffLatched;

        // echo
        readonly short[] echoBuf = new short[15 * 512 * 2 + 2];   // EDL 15 = 240 ms, stereo
        int echoPos, echoLength = 1;                              // in stereo frames
        readonly int[] histL = new int[16], histR = new int[16];
        int histPos;

        public int VoiceMuteMask;       // voices to leave out of the mix (testing)

        public SnesDsp()
        {
            for (int i = 0; i < 8; i++) voices[i] = new Voice();
            Reset();
        }

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            regs[FLG] = 0xE0;
            foreach (var v in voices)
            {
                Array.Clear(v.Buf, 0, v.Buf.Length);
                v.BufPos = 0; v.InterpPos = 0; v.Sample = null; v.BlockOff = 0; v.BrrOff = 1;
                v.KonDelay = 0; v.Mode = EnvMode.Release; v.Env = 0; v.HiddenEnv = 0; v.Silent = true;
            }
            counter = 0; noise = 0x4000; everyOther = false; konLatched = newKon = koffLatched = 0;
            Array.Clear(echoBuf, 0, echoBuf.Length);
            echoPos = 0; echoLength = 1; histPos = 0;
            Array.Clear(histL, 0, 16); Array.Clear(histR, 0, 16);
        }

        public byte Read(int reg) => regs[reg & 0x7F];

        public void Write(int reg, int value)
        {
            reg &= 0x7F;
            regs[reg] = (byte)value;
            if (reg == KON) newKon = value & 0xFF;
            else if (reg == ENDX) regs[ENDX] = 0;
        }

        static int Clamp16(int v) => v > 32767 ? 32767 : v < -32768 ? -32768 : v;

        bool ReadCounter(int rate) => ((counter + CounterOffsets[rate]) % CounterRates[rate]) != 0;

        // Render `frames` stereo samples into out[offset..] (interleaved L, R).
        public void Render(short[] output, int offset, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                int l, r;
                RunSample(out l, out r);
                output[offset++] = (short)l;
                output[offset++] = (short)r;
            }
        }

        void RunSample(out int outL, out int outR)
        {
            // global counter and noise
            if (--counter < 0) counter = CounterRange - 1;
            if (!ReadCounter(regs[FLG] & 0x1F))
            {
                int feedback = (noise << 13) ^ (noise << 14);
                noise = (feedback & 0x4000) ^ (noise >> 1);
            }

            everyOther = !everyOther;
            if (everyOther)
            {
                newKon &= ~konLatched;          // a KON bit is consumed once it has been seen
                konLatched = newKon;
                koffLatched = regs[KOF];
            }
            int konNow = everyOther ? konLatched : 0;

            int pmon = regs[PMON] & 0xFE, non = regs[NON], eon = regs[EON], flg = regs[FLG];
            int mainL = 0, mainR = 0, echoL = 0, echoR = 0;
            int prevOut = 0;
            int endx = regs[ENDX];

            for (int vi = 0; vi < 8; vi++)
            {
                var v = voices[vi];
                int vbit = 1 << vi;
                int vr = vi << 4;

                if (v.Silent && (konNow & vbit) == 0)
                {
                    prevOut = 0;
                    continue;
                }

                int pitch = (regs[vr + PITCHL] | (regs[vr + PITCHH] << 8)) & 0x3FFF;
                if ((pmon & vbit) != 0)
                    pitch += ((prevOut >> 5) * pitch) >> 10;

                int header;
                if (v.KonDelay > 0)
                {
                    if (v.KonDelay == 5)
                    {
                        v.Sample = Sources[regs[vr + SRCN]];
                        v.BlockOff = 0;
                        v.BrrOff = 1;
                        v.BufPos = 0;
                        header = 0;
                        endx &= ~vbit;
                    }
                    else header = CurrentHeader(v);
                    v.Env = 0;
                    v.HiddenEnv = 0;
                    v.InterpPos = 0;
                    if ((--v.KonDelay & 3) != 0) v.InterpPos = 0x4000;
                    pitch = 0;
                }
                else header = CurrentHeader(v);

                // output: Gaussian interpolation (or noise), times the envelope
                int output;
                if ((non & vbit) != 0)
                    output = (short)(noise * 2);
                else
                {
                    int off = (v.InterpPos >> 4) & 0xFF;
                    int fwd = 255 - off;
                    int p = (v.InterpPos >> 12) + v.BufPos;
                    var b = v.Buf;
                    int o = (Gauss[fwd] * b[p]) >> 11;
                    o += (Gauss[fwd + 256] * b[p + 1]) >> 11;
                    o += (Gauss[off + 256] * b[p + 2]) >> 11;
                    o = (short)o;
                    o += (Gauss[off] * b[p + 3]) >> 11;
                    output = Clamp16(o) & ~1;
                }
                output = ((output * v.Env) >> 11) & ~1;
                prevOut = output;

                // end of sample without a loop, or soft reset: immediate silence
                if ((flg & 0x80) != 0 || (header & 3) == 1)
                {
                    v.Mode = EnvMode.Release;
                    v.Env = 0;
                }
                if (everyOther)
                {
                    if ((koffLatched & vbit) != 0) v.Mode = EnvMode.Release;
                    if ((konNow & vbit) != 0)
                    {
                        v.KonDelay = 5;
                        v.Mode = EnvMode.Attack;
                        v.Silent = false;
                    }
                }
                if (v.KonDelay == 0) RunEnvelope(v, vr);

                // decode BRR as the interpolation position needs it
                if (v.InterpPos >= 0x4000 && v.Sample != null)
                {
                    DecodeBrr(v, header);
                    if ((v.BrrOff += 2) >= 9)
                    {
                        v.BlockOff += 9;
                        if ((header & 1) != 0)
                        {
                            // the end flag: continue at the loop point of the current SRCN's sample
                            var s = Sources[regs[vr + SRCN]] ?? v.Sample;
                            v.Sample = s;
                            v.BlockOff = s.Loop;
                            endx |= vbit;
                        }
                        if (v.BlockOff >= v.Sample.Data.Length) v.BlockOff = v.Sample.Loop;
                        v.BrrOff = 1;
                    }
                }
                v.InterpPos = (v.InterpPos & 0x3FFF) + pitch;
                if (v.InterpPos > 0x7FFF) v.InterpPos = 0x7FFF;

                if ((VoiceMuteMask & vbit) == 0)
                {
                    int al = (output * (sbyte)regs[vr + VOLL]) >> 7;
                    int ar = (output * (sbyte)regs[vr + VOLR]) >> 7;
                    mainL = Clamp16(mainL + al);
                    mainR = Clamp16(mainR + ar);
                    if ((eon & vbit) != 0)
                    {
                        echoL = Clamp16(echoL + al);
                        echoR = Clamp16(echoR + ar);
                    }
                }

                if (v.Mode == EnvMode.Release && v.Env == 0 && v.KonDelay == 0) v.Silent = true;
            }
            regs[ENDX] = (byte)endx;

            // echo: read the delayed sample, FIR it, mix, feed back
            if (++histPos >= 8) histPos = 0;
            int ei = echoPos * 2;
            histL[histPos] = histL[histPos + 8] = echoBuf[ei] >> 1;
            histR[histPos] = histR[histPos + 8] = echoBuf[ei + 1] >> 1;
            int fl = 0, fr = 0;
            for (int t = 0; t < 7; t++)
            {
                int c = (sbyte)regs[FIR + (t << 4)];
                fl += (histL[histPos + t + 1] * c) >> 6;
                fr += (histR[histPos + t + 1] * c) >> 6;
            }
            {
                int c = (sbyte)regs[FIR + 0x70];
                fl = (short)fl + (short)((histL[histPos + 8] * c) >> 6);
                fr = (short)fr + (short)((histR[histPos + 8] * c) >> 6);
            }
            fl = Clamp16(fl) & ~1;
            fr = Clamp16(fr) & ~1;

            outL = Clamp16((short)((mainL * (sbyte)regs[MVOLL]) >> 7) + (short)((fl * (sbyte)regs[EVOLL]) >> 7));
            outR = Clamp16((short)((mainR * (sbyte)regs[MVOLR]) >> 7) + (short)((fr * (sbyte)regs[EVOLR]) >> 7));

            if ((flg & 0x20) == 0)
            {
                int efb = (sbyte)regs[EFB];
                echoBuf[ei] = (short)(Clamp16(echoL + (short)((fl * efb) >> 7)) & ~1);
                echoBuf[ei + 1] = (short)(Clamp16(echoR + (short)((fr * efb) >> 7)) & ~1);
            }
            if (++echoPos >= echoLength)
            {
                echoPos = 0;
                int edl = regs[EDL] & 0x0F;
                echoLength = edl == 0 ? 1 : edl * 512;
            }

            if ((flg & 0x40) != 0) { outL = 0; outR = 0; }
        }

        static int CurrentHeader(Voice v) =>
            v.Sample == null ? 1 : v.Sample.Data[v.BlockOff < v.Sample.Data.Length ? v.BlockOff : 0];

        void DecodeBrr(Voice v, int header)
        {
            var data = v.Sample.Data;
            int at = v.BlockOff + v.BrrOff;
            int nybbles = (data[at] << 8) | (at + 1 < data.Length ? data[at + 1] : 0);
            int shift = header >> 4;
            int filter = header & 0x0C;
            var buf = v.Buf;
            int pos = v.BufPos;
            if ((v.BufPos += 4) >= 12) v.BufPos = 0;
            for (int end = pos + 4; pos < end; pos++, nybbles <<= 4)
            {
                int s = (short)nybbles >> 12;
                s = (s << shift) >> 1;
                if (shift >= 0xD) s = (s >> 25) << 11;
                int p1 = buf[pos + 11];
                int p2 = buf[pos + 10] >> 1;
                if (filter >= 8)
                {
                    s += p1;
                    s -= p2;
                    if (filter == 8) { s += p2 >> 4; s += (p1 * -3) >> 6; }
                    else { s += (p1 * -13) >> 7; s += (p2 * 3) >> 4; }
                }
                else if (filter != 0)
                {
                    s += p1 >> 1;
                    s += (-p1) >> 5;
                }
                s = Clamp16(s);
                s = (short)(s * 2);
                buf[pos + 12] = buf[pos] = s;
            }
        }

        void RunEnvelope(Voice v, int vr)
        {
            int env = v.Env;
            if (v.Mode == EnvMode.Release)
            {
                if ((env -= 8) < 0) env = 0;
                v.Env = env;
                return;
            }
            int rate;
            int adsr0 = regs[vr + ADSR1];
            int envData = regs[vr + ADSR2];
            if ((adsr0 & 0x80) != 0)
            {
                if (v.Mode >= EnvMode.Decay)
                {
                    env--;
                    env -= env >> 8;
                    rate = envData & 0x1F;
                    if (v.Mode == EnvMode.Decay) rate = ((adsr0 >> 3) & 0x0E) + 0x10;
                }
                else
                {
                    rate = (adsr0 & 0x0F) * 2 + 1;
                    env += rate < 31 ? 0x20 : 0x400;
                }
            }
            else
            {
                envData = regs[vr + GAIN];
                int mode = envData >> 5;
                if (mode < 4)
                {
                    env = envData * 0x10;
                    rate = 31;
                }
                else
                {
                    rate = envData & 0x1F;
                    if (mode == 4) env -= 0x20;
                    else if (mode < 6) { env--; env -= env >> 8; }
                    else
                    {
                        env += 0x20;
                        if (mode > 6 && (uint)v.HiddenEnv >= 0x600) env += 0x8 - 0x20;
                    }
                }
            }
            if ((env >> 8) == (envData >> 5) && v.Mode == EnvMode.Decay) v.Mode = EnvMode.Sustain;
            v.HiddenEnv = env;
            if ((uint)env > 0x7FF)
            {
                env = env < 0 ? 0 : 0x7FF;
                if (v.Mode == EnvMode.Attack) v.Mode = EnvMode.Decay;
            }
            if (!ReadCounter(rate)) v.Env = env;
        }

        // How many voices are sounding (for the harness).
        public int ActiveVoices
        {
            get { int n = 0; foreach (var v in voices) if (!v.Silent) n++; return n; }
        }
    }
}
