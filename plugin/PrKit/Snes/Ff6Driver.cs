using System;

namespace PrKit.Snes
{
    // One instrument of a song: its BRR sample and the sound driver's per-instrument data (the
    // two-byte pitch multiplier and the default ADSR).
    public sealed class Ff6Instrument
    {
        public BrrSample Sample;
        public byte Tune0, Tune1;      // pitch multiplier: Tune0 < $80: 1 + Tune0/256, else Tune0/256; Tune1 a finer fraction
        public byte Adsr1, Adsr2;      // DSP ADSR registers (Adsr1 bit 7 set: ADSR mode)
    }

    // A song in FF6's sequence format ("AKAO"): the bytes the driver keeps at $1C00 (a 0x24-byte
    // header: the address the song was assembled at, the "no voice" marker, 8 voice pointers and 8
    // alternate-start pointers) and its 16 instruments (programs $20-$2F).
    public sealed class Ff6Song
    {
        public byte[] Data;
        public readonly Ff6Instrument[] Instruments = new Ff6Instrument[16];
        public string Name;
    }

    // FINAL FANTASY VI's SNES sound driver (the "AKAO" sequencer on the SPC700), re-implemented in
    // C# from the community disassembly's description of its behaviour (everything8215/ff6,
    // src/spc/spc_main.asm): the same tick (timer 0 at 39 * 125 us = 4.875 ms = 156 DSP samples),
    // tempo accumulator, note table, key-off two ticks before a note ends unless the next note is
    // tied or slurred (found by looking ahead in the script), volume/pan/pitch envelopes, vibrato,
    // tremolo and pan sweep with their fade-in, loops and second endings, echo volume/feedback/FIR
    // fades, the song suspend/resume used for battles, and the master volume / tempo / pitch
    // controls the game side sends. The arithmetic keeps the driver's 8/16/24-bit widths so songs
    // sound as they do on the console. It drives an SnesDsp directly (no SPC700 is emulated): no
    // part of Square's program is shipped, only this description of what it does.
    //
    // Sound effects (the driver's other 8 "voices") are the game's own business and not here; a
    // song command that plays a game sound effect (E9/EA) ends that voice instead.
    public sealed class Ff6Driver
    {
        public const int SamplesPerTick = 156;

        static readonly int[] FreqMult = { 0x0879, 0x08fa, 0x0983, 0x0a14, 0x0aad, 0x0b50, 0x0bfc, 0x0cb2, 0x0d74, 0x0e41, 0x0f1a, 0x1000, 0x10f3 };
        static readonly byte[] NoteDur = { 0xc0, 0x60, 0x40, 0x48, 0x30, 0x20, 0x24, 0x18, 0x10, 0x0c, 0x08, 0x06, 0x04, 0x03 };
        static readonly byte[] FilterTbl = {
            0x7f, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x0c, 0x21, 0x2b, 0x2b, 0x13, 0xfe, 0xf3, 0xf9,
            0x58, 0xbf, 0xdb, 0xf0, 0xfe, 0x07, 0x0c, 0x0c,
            0x34, 0x33, 0x00, 0xd9, 0xe5, 0x01, 0xfc, 0xeb,
            0x80, 0x80, 0x80, 0x80, 0x7f, 0x7f, 0x7f, 0x7f };
        // parameter bytes of commands $C4-$FF
        static readonly byte[] CmdBytes = {
            1, 2, 1, 2, 2, 3, 0, 3, 0, 2, 0, 1,
            0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1,
            1, 0, 1, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0,
            1, 2, 1, 2, 1, 3, 2, 2, 2, 0, 0, 0, 2, 0, 0, 0 };

        // Everything the driver keeps for one song; the driver swaps it out when a battle song
        // suspends the field song and back when that song is asked for again.
        public sealed class State
        {
            public int Id = -1;
            public Ff6Song Song;
            public int Offset;                        // added to the script's addresses
            public int[] Ptr = new int[8], Dur = new int[8], LoopSp = new int[8];
            public int[] Octave = new int[8], Program = new int[8];
            public int[] Vol = new int[8], VolRate = new int[8], VolCount = new int[8];
            public int[] Pan = new int[8], PanRate = new int[8], PanCount = new int[8];
            public int[] VibCycle = new int[8], VibCount = new int[8], VibMax = new int[8], VibRate = new int[8],
                VibAcc = new int[8], VibOffset = new int[8], VibDelay = new int[8], VibDelayCount = new int[8], VibParam = new int[8];
            public int[] TremCycle = new int[8], TremCount = new int[8], TremMax = new int[8], TremRate = new int[8],
                TremAcc = new int[8], TremDelay = new int[8], TremDelayCount = new int[8], TremParam = new int[8];
            public int[] SweepCycle = new int[8], SweepCount = new int[8], SweepRate = new int[8], SweepAcc = new int[8], SweepParam = new int[8];
            public int[] Gain = new int[8];  // vibrato fade-in steps (bits 0-2), tremolo's (4-6)
            public int[] GlissTicks = new int[8], Gliss = new int[8], GlissRate = new int[8], GlissRateLo = new int[8], GlissFrac = new int[8];
            public int[] Transpose = new int[8], Detune = new int[8], PitchIndex = new int[8], Freq = new int[8];
            public int[] Tune0 = new int[8], Tune1 = new int[8], AdsrA = new int[8], AdsrB = new int[8];
            public int[,] LoopIter = new int[8, 4], LoopLeft = new int[8, 4], LoopAddr = new int[8, 4];
            public int Tempo, TempoAcc, TempoCount, TempoRate;
            public int EchoVol, EchoVolRate, EchoVolCount;
            public int SongVol = 0xff;
            public int Active, EchoMask, NoiseMask, PmonMask;
            public int Tie, Slur, SlurOn, Roll, IgnoreVol;
            public int NoiseClock;
            public int[] Fir = new int[8], FirRate = new int[8];
            public int Feedback, FeedbackRate, FeedbackCount, FirCount;
            public int OutputCode;
            public int KonBuf, Keyed, KofBuf;

            public State Clone()
            {
                var c = (State)MemberwiseClone();
                foreach (var f in typeof(State).GetFields())
                    if (f.FieldType.IsArray) f.SetValue(c, ((Array)f.GetValue(this)).Clone());
                return c;
            }
        }

        readonly SnesDsp dsp;
        State s = new State();
        State paused;                               // the song a battle song suspended
        public int PausedId => paused?.Id ?? -1;
        public int CurrentId => s.Id;
        public State Current => s;

        // the program tables the game side fills from the song's instruments
        readonly byte[] progTune0 = new byte[256], progTune1 = new byte[256], progAdsr1 = new byte[256], progAdsr2 = new byte[256];

        // Beyond Chaos's mfvitools patch to the lookahead ("ffmode" shadow hack): after a loop start
        // is seen ahead, loop ends and second endings are no longer followed; the game sound effect
        // commands are skipped instead of followed. BC's ROMs always carry it.
        public bool LookaheadPatch = true;

        // game-side controls (interrupts $81, $84, $85, $89), not saved with a song
        int masterVol = 0xff00, masterRate, masterCount;
        int tempoRatio, pitchRatio;
        int condJump;
        int envTicks = 7;
        int dirtyVol, dirtyFreq;
        int flgBuf;

        // On the console a song change keys every voice off and then spends a while moving the new
        // song's samples and script into the sound chip's RAM; these ticks stand in for that gap.
        public int TransferTicks = 8;
        int holdTicks;

        public int TicksPlayed;
        public string LastWarning;

        // SPC700 time: the console's driver can't always finish a tick's work in its 4,992 cycles
        // (busy songs, chords with vibrato), and a tick that overruns delays the next (the main loop
        // polls the timer and runs once per poll), so busy songs play a little slower than written.
        // Cycles is the estimated cost of the last Tick, from the driver's code paths (counted from
        // the disassembly, then scaled to recordings: tools/snes_song_ref.py); the player schedules
        // the next tick from it (SnesMusicPlayer.ModelCpuTime).
        public int Cycles;
        public static class Cost
        {
            // counted from the disassembly's code paths, then the three groups (each tick's base work x0.85,
            // a note's x0.875, the voice updates x1) fitted to 22 Snes9x recordings of BC songs (Snes.Tests
            // fitcost, 2026-11-20: RMS tempo error 0.19%)
            public static double Scale = 1.0;
            public static int Round = 748, Envelope = 51, SongTick = 102, SongTickVoice = 128, KeyOff = 34,
                Note = 831, Command = 127, Param = 28, Lookahead = 79, Octave = 15, EffectInit = 105,
                FreqVol = 50, Effect = 60, VibChange = 140, TremChange = 20, EffectTurn = 80,
                VolUpdate = 280, FreqUpdate = 190, Adsr = 39;
        }

        public Ff6Driver(SnesDsp dsp)
        {
            this.dsp = dsp;
            dsp.Write(SnesDsp.MVOLL, 0x3f);
            dsp.Write(SnesDsp.MVOLR, 0x3f);
            dsp.Write(SnesDsp.DIR, 0x1b);
            dsp.Write(SnesDsp.EDL, 5);
            dsp.Write(SnesDsp.FLG, 0);
            for (int v = 0; v < 8; v++) dsp.Write((v << 4) | SnesDsp.GAIN, 0xa0);
            SetResident(null);
        }

        // Programs $00-$07, the driver's resident waveforms: FF6's own where given (a pack's "resident"
        // rows), else the synthesized stand-ins (SystemWaves).
        public void SetResident(Ff6Instrument[] resident)
        {
            for (int p = 0; p < 8; p++)
            {
                var ins = resident != null && p < resident.Length && resident[p] != null ? resident[p] : SystemWaves.Make(p);
                dsp.Sources[p] = ins.Sample;
                progTune0[p] = ins.Tune0; progTune1[p] = ins.Tune1;
                progAdsr1[p] = ins.Adsr1; progAdsr2[p] = ins.Adsr2;
            }
        }

        // Echo delay in 16 ms steps (FF6: 5).
        public void SetEchoDelay(int edl) => dsp.Write(SnesDsp.EDL, edl & 0x0f);

        void LoadInstruments(Ff6Song song)
        {
            for (int i = 0; i < 16; i++)
            {
                var ins = song.Instruments[i];
                int p = 0x20 + i;
                dsp.Sources[p] = ins?.Sample;
                progTune0[p] = ins?.Tune0 ?? 0;
                progTune1[p] = ins?.Tune1 ?? 0;
                progAdsr1[p] = ins?.Adsr1 ?? 0x8f;
                progAdsr2[p] = ins?.Adsr2 ?? 0xe0;
            }
        }

        // Play a song (the game's $10/$11 command; $14/$15 with suspendCurrent: battle songs keep the
        // song they interrupt, and asking for that song again resumes it where it was, fading in).
        public void Play(int id, Ff6Song song, int volume = 0xff, bool alternateStart = false, bool suspendCurrent = false)
        {
            dsp.Write(SnesDsp.KOF, 0xff);
            holdTicks = TransferTicks;
            masterVol = (masterVol & 0xff) | ((volume & 0xff) << 8);
            masterCount = 0;
            if (suspendCurrent && s.Song != null && s.Id != 0x2f)
                paused = s.Clone();
            if (paused != null && paused.Id == id)
            {
                Resume();
                return;
            }
            LoadInstruments(song);
            var n = s.Clone();
            n.Id = id;
            n.Song = song;
            n.Active = 0; n.EchoMask = 0; n.NoiseMask = 0; n.PmonMask = 0;
            n.KonBuf = 0; n.KofBuf = 0; n.Keyed = 0;
            n.Tie = 0; n.Slur = 0; n.Roll = 0; n.IgnoreVol = 0;
            n.Tempo = (n.Tempo & 0xff) | 0x0100;
            n.TempoAcc = 0xff;
            n.SongVol = 0xff;
            n.OutputCode = 0;
            var d = song.Data;
            int baseAddr = d[0] | (d[1] << 8);
            n.Offset = (0x1c24 - baseAddr) & 0xffff;
            int marker = d[2] | (d[3] << 8);
            int hdr = alternateStart ? 0x14 : 0x04;
            for (int v = 7; v >= 0; v--)
            {
                int p = d[hdr + 2 * v] | (d[hdr + 2 * v + 1] << 8);
                if (p == marker) continue;
                n.Active |= 1 << v;
                n.Ptr[v] = (p + n.Offset) & 0xffff;
                InitChan(n, v);
            }
            s = n;
            dirtyVol = dirtyFreq = 0;
        }

        void Resume()
        {
            s = paused;
            paused = null;
            LoadInstruments(s.Song);
            flgBuf = s.NoiseClock & 0x1f;
            for (int v = 0; v < 8; v++)
            {
                s.Dur[v] = (s.Dur[v] + 1) & 0xff;
                UpdateSample(v);
                dsp.Write((v << 4) | SnesDsp.VOLL, 0);
                dsp.Write((v << 4) | SnesDsp.VOLR, 0);
                UpdateChanFreq(v);
            }
            s.KonBuf = s.Keyed;
            s.KofBuf = 0;
            dirtyVol = dirtyFreq = 0;
            // the driver's own fade-in: master volume from $20 to $ff over 16 envelope steps
            masterVol = 0x2000;
            FadeMaster(16, 0xff);
            dirtyVol = 0xff;
        }

        // Stop the song (keepPaused: a suspended song stays for Resume, as a battle's end leaves the field's).
        public void Stop(bool keepPaused = false)
        {
            dsp.Write(SnesDsp.KOF, 0xff);
            s.Active = 0;
            s.KonBuf = 0;
            s.KofBuf = 0xff;
            s.Id = -1;
            s.Song = null;
            if (!keepPaused) paused = null;
        }

        // Keep the current song to resume later (what a battle song's start does on the console) and
        // go silent.
        public void SuspendCurrent()
        {
            if (s.Song == null) return;
            if (s.Id != 0x2f) paused = s.Clone();
            Stop(keepPaused: true);
        }

        // Back to the suspended song where it was, fading in (false: none).
        public bool ResumePaused()
        {
            if (paused == null) return false;
            dsp.Write(SnesDsp.KOF, 0xff);
            holdTicks = TransferTicks;
            Resume();
            return true;
        }

        public void DropPaused() => paused = null;

        // Interrupt $81: fade the master volume to `target` over `steps` envelope steps (7 ticks each).
        public void FadeMaster(int steps, int target)
        {
            masterCount = steps & 0xff;
            if (masterCount == 0)
            {
                masterVol = (target & 0xff) << 8;
                masterRate = 0;
                return;
            }
            int cur = masterVol >> 8;
            int diff = (target & 0xff) - cur;
            if (diff == 0) { masterCount = 0; return; }
            masterRate = CalcEnvRate(diff & 0xff, diff >= 0, masterCount);
            masterVol &= 0xff00;
            dirtyVol = 0xff;
        }

        // Interrupt $84: tempo ratio, a signed fraction ($80 = half speed, $40 = 1.5x).
        public void SetTempoRatio(int ratio) => tempoRatio = ratio & 0xff;
        // Interrupt $85: pitch ratio, a signed fraction.
        public void SetPitchRatio(int ratio) { pitchRatio = ratio & 0xff; dirtyFreq = 0xff; }
        // Interrupt $89: let each voice take its next conditional jump (FC).
        public void EnableConditionalJump() => condJump = s.Active;
        public int OutputCode => s.OutputCode;
        public int ActiveVoices => s.Active;

        // ---------------------------------------------------------------------------------- tick

        // One timer tick of the driver's main loop (4.875 ms): write the buffered DSP registers, run
        // the scripts, refresh voices' pitch and volume. The caller renders 156 samples after it.
        public void Tick()
        {
            TicksPlayed++;
            Cycles = Cost.Round;
            if (holdTicks > 0)
            {
                holdTicks--;
                dsp.Write(SnesDsp.KOF, 0xff);
                return;
            }
            for (int i = 7; i >= 0; i--) dsp.Write(SnesDsp.FIR + (i << 4), s.Fir[i] >> 8);
            dsp.Write(SnesDsp.EFB, s.Feedback >> 8);
            dsp.Write(SnesDsp.EVOLR, s.EchoVol >> 8);
            dsp.Write(SnesDsp.EVOLL, s.EchoVol >> 8);
            dsp.Write(SnesDsp.EON, s.EchoMask);
            dsp.Write(SnesDsp.KOF, s.KofBuf);
            dsp.Write(SnesDsp.FLG, flgBuf);
            dsp.Write(SnesDsp.NON, s.NoiseMask);
            dsp.Write(SnesDsp.PMON, s.PmonMask);
            dsp.Write(SnesDsp.KON, s.KonBuf);
            s.KofBuf = 0;
            s.KonBuf = 0;

            if (--envTicks == 0)
            {
                envTicks = 7;
                Cycles += Cost.Envelope;
                if (masterCount != 0)
                {
                    masterCount--;
                    int before = masterVol >> 8;
                    masterVol = (masterVol + masterRate) & 0xffff;
                    if ((masterVol >> 8) != before) dirtyVol |= s.Active;
                }
            }
            if (s.Song != null)
            {
                UpdateScripts();
                int m = s.Active & s.Keyed;
                for (int v = 0; v < 8; v++)
                    if ((m & (1 << v)) != 0) UpdateFreqVol(v);
            }
            dirtyVol = dirtyFreq = 0;
        }

        void UpdateScripts()
        {
            int t = s.Tempo >> 8;
            if (tempoRatio != 0)
            {
                int hi = (t * tempoRatio) >> 8;
                if ((tempoRatio & 0x80) == 0)
                {
                    t = ((hi << 1) & 0xff) + t;
                    if (t > 0xff) t = 0xff;
                }
                else t = hi == 0 ? 1 : hi;
            }
            s.TempoAcc += t;
            if (s.TempoAcc <= 0xff) return;
            s.TempoAcc &= 0xff;
            Cycles += Cost.SongTick;

            int voices = s.Active;
            for (int v = 0; v < 8; v++)
            {
                int bit = 1 << v;
                if ((voices & bit) == 0) continue;
                Cycles += Cost.SongTickVoice;
                s.Dur[v] = (s.Dur[v] - 1) & 0xff;
                if (s.Dur[v] == 0) ExecScript(v);
                else if (s.Dur[v] == 2 && (s.Tie & bit) == 0)
                {
                    s.Keyed &= ~bit;
                    s.KofBuf |= bit;
                    Cycles += Cost.KeyOff;
                }
                UpdateChanEnv(v);
            }
            UpdateSongEnv();
        }

        void UpdateSongEnv()
        {
            if (s.TempoCount != 0) { s.TempoCount--; s.Tempo = (s.Tempo + s.TempoRate) & 0xffff; }
            if (s.EchoVolCount != 0) { s.EchoVolCount--; s.EchoVol = (s.EchoVol + s.EchoVolRate) & 0xffff; }
            if (s.FeedbackCount != 0) { s.FeedbackCount--; s.Feedback = (s.Feedback + s.FeedbackRate) & 0xffff; }
            if (s.FirCount != 0)
            {
                s.FirCount--;
                for (int i = 0; i < 8; i++) s.Fir[i] = (s.Fir[i] + s.FirRate[i]) & 0xffff;
            }
        }

        // ----------------------------------------------------------------------------- scripts

        int Peek(int addr)
        {
            int i = (addr & 0xffff) - 0x1c00;
            var d = s.Song.Data;
            return i >= 0 && i < d.Length ? d[i] : 0xeb;
        }

        int NextParam(int v)
        {
            int a = Peek(s.Ptr[v]);
            s.Ptr[v] = (s.Ptr[v] + 1) & 0xffff;
            return a;
        }

        void ExecScript(int v)
        {
            int bit = 1 << v;
            int n;
            Cycles += Cost.Note;
            while (true)
            {
                n = NextParam(v);
                if (n < 0xc4) break;
                Cycles += Cost.Command + Cost.Param * Math.Max(0, CmdBytes[n - 0xc4] - 1);
                if (!ExecCmd(v, n)) return;          // the voice ended
            }
            if (s.Dur[v] == 0) s.Dur[v] = NoteDur[n % 14];
            if (n >= 0xa8)
            {
                if (n >= 0xb6) return;                // rest
                AfterNote(v);                         // tie: the note goes on
                return;
            }
            int semi = n / 14;
            s.PitchIndex[v] = ((s.Octave[v] * 12) + semi + s.Transpose[v] - 10) & 0xff;
            s.Freq[v] = CalcFreq(v, s.PitchIndex[v]);
            if (s.VibParam[v] != 0) InitVibrato(v, s.VibParam[v]);
            if (s.TremParam[v] != 0) InitTremolo(v, s.TremParam[v]);
            s.VibOffset[v] = 0;
            s.TremAcc[v] = 0;
            s.GlissRate[v] = 0;
            s.Keyed |= bit;
            if ((s.Roll & bit) != 0) s.KonBuf |= bit;
            else if ((s.Slur & bit) != 0)
            {
                if ((s.SlurOn & bit) == 0) { s.SlurOn |= bit; s.KonBuf |= bit; }
            }
            else s.KonBuf |= bit;
            dirtyVol |= bit;
            dirtyFreq |= bit;
            UpdateSample(v);
            AfterNote(v);
        }

        void AfterNote(int v)
        {
            int bit = 1 << v;
            int next = FindNextNote(v);
            if (next >= 0xa8 && next < 0xb6) s.Tie |= bit;
            else
            {
                if (next >= 0xb6) { s.Slur &= ~bit; s.Roll &= ~bit; }
                s.Tie = (s.Tie & ~bit) | ((s.Slur | s.Roll) & bit);
            }
            if (s.Gliss[v] == 0) return;
            s.PitchIndex[v] = (s.PitchIndex[v] + s.Gliss[v]) & 0xff;
            int target = CalcFreq(v, s.PitchIndex[v]);
            int diff = (target - s.Freq[v]) & 0xffff;
            bool up = target >= s.Freq[v];
            if (!up) diff = (-diff) & 0xffff;
            int hi, mid, lo;
            int ticks = s.GlissTicks[v];
            if (ticks == 0) { hi = 0; mid = diff >> 8; lo = diff & 0xff; }
            else
            {
                int y = 0, a = diff >> 8;
                hi = ((y << 8) | a) / ticks; y = ((y << 8) | a) % ticks;
                a = diff & 0xff;
                mid = ((y << 8) | a) / ticks; y = ((y << 8) | a) % ticks;
                lo = (y << 8) / ticks;
                hi &= 0xff; mid &= 0xff; lo &= 0xff;
            }
            int rate24 = (hi << 16) | (mid << 8) | lo;
            if (!up) rate24 = (-rate24) & 0xffffff;
            s.GlissRate[v] = rate24 >> 8;            // f781:f780
            s.GlissRateLo[v] = rate24 & 0xff;        // f8a0
            s.Gliss[v] = 0;
            s.GlissFrac[v] = 0;
        }

        int CalcFreq(int v, int index)
        {
            int oct = index / 12, semi = index % 12;
            int mult = FreqMult[semi];
            int mhi = mult >> 8, mlo = mult & 0xff;
            int sum = s.Tune0[v] + s.Detune[v];
            int t = sum & 0xff;
            bool negative = (t & 0x80) != 0;
            int c = (mhi * t) & 0xffff;
            c = (c + ((mlo * t) >> 8)) & 0xffff;
            int r;
            if (s.Tune1[v] != 0) r = (((c >> 8) * s.Tune1[v]) >> 8) + c;
            else r = c;
            if (!negative) r += mult;
            r &= 0xffff;
            if (oct > 4) for (int i = 4; i < oct; i++) r = (r << 1) & 0xffff;
            else for (int i = oct; i < 4; i++) r >>= 1;
            Cycles += Cost.Octave * Math.Abs(oct - 4);
            return r;
        }

        // the next note this voice will play (a rest/tie/end decides whether this one keys off)
        int FindNextNote(int v)
        {
            int p = s.Ptr[v];
            int c5 = s.LoopSp[v];
            int guard = 0;
            while (guard++ < 4096)
            {
                int a = Peek(p);
                if (a < 0xc4) return a;
                Cycles += Cost.Lookahead;
                p = (p + 1) & 0xffff;
                if (a == 0xeb) return a;
                if (a == 0xf6) { p = (Peek(p) | (Peek(p + 1) << 8)) + s.Offset & 0xffff; continue; }
                if (LookaheadPatch)
                {
                    if (c5 != 0xff)
                    {
                        if (a == 0xe2) c5 = 0xff;
                        else if (a == 0xe3) { p = LookLoopEnd(v, p, ref c5); continue; }
                        else if (a == 0xf5) { p = LookSecondEnding(v, p, ref c5); continue; }
                    }
                    if (a == 0xe5) { s.Slur &= ~(1 << v); continue; }
                    if (a == 0xe7) { s.Roll &= ~(1 << v); continue; }
                }
                else
                {
                    if (a == 0xe3) { p = LookLoopEnd(v, p, ref c5); continue; }
                    if (a == 0xf5) { p = LookSecondEnding(v, p, ref c5); continue; }
                    if (a == 0xe5) { s.Slur &= ~(1 << v); continue; }
                    if (a == 0xe7) { s.Roll &= ~(1 << v); continue; }
                    if (a == 0xe9 || a == 0xea) return 0xeb;   // a game sound effect follows: not ours
                }
                if (a == 0xdc)
                {
                    s.Slur &= ~(1 << v);
                    s.Roll &= ~(1 << v);
                    continue;                         // (the driver then reads the program as the next byte)
                }
                p = (p + CmdBytes[a - 0xc4]) & 0xffff;
            }
            return 0xeb;
        }

        int LookLoopEnd(int v, int p, ref int c5)
        {
            int left = s.LoopLeft[v, c5 & 3];
            if (left != 0 && ((left - 1) & 0xff) == 0)
            {
                c5 = (c5 - 1) & 3;
                return p;
            }
            return s.LoopAddr[v, c5 & 3];
        }

        int LookSecondEnding(int v, int p, ref int c5)
        {
            if (s.LoopIter[v, c5 & 3] == Peek(p))
            {
                if (((s.LoopLeft[v, c5 & 3] - 1) & 0xff) == 0) c5 = (c5 - 1) & 3;
                return (Peek(p + 1) | (Peek(p + 2) << 8)) + s.Offset & 0xffff;
            }
            return (p + 3) & 0xffff;
        }

        static int CalcEnvRate(int diff, bool notNegative, int duration)
        {
            int a = diff & 0xff;
            if (!notNegative) a = (-a) & 0xff;
            int hi = a / duration, rem = a % duration;
            int lo = (rem << 8) / duration;
            int r = ((hi & 0xff) << 8) | (lo & 0xff);
            if (!notNegative) r = (-r) & 0xffff;
            return r;
        }

        // returns false when the voice ends
        bool ExecCmd(int v, int cmd)
        {
            int bit = 1 << v;
            int p = CmdBytes[cmd - 0xc4] != 0 ? NextParam(v) : 0;
            switch (cmd)
            {
                case 0xc4: SetVolume(v, p); break;
                case 0xc5:
                {
                    s.VolCount[v] = p;
                    int dur = p;
                    int target = NextParam(v) & 0x7f;
                    if (dur == 0) { SetVolume(v, target); break; }
                    int cur = s.Vol[v] >> 8;
                    int diff = target - cur;
                    if ((diff & 0xff) == 0) { s.VolCount[v] = 0; break; }
                    s.VolRate[v] = CalcEnvRate(diff, diff >= 0, dur);
                    break;
                }
                case 0xc6: SetPan(v, p); break;
                case 0xc7:
                {
                    s.PanCount[v] = p;
                    int dur = p;
                    int target = NextParam(v);
                    if (dur == 0) { SetPan(v, target); break; }
                    target = (target << 1) & 0xff;
                    int cur = s.Pan[v] >> 8;
                    int diff = target - cur;
                    if ((diff & 0xff) == 0) { s.PanCount[v] = 0; break; }
                    s.PanRate[v] = CalcEnvRate(diff, diff >= 0, dur);
                    break;
                }
                case 0xc8:
                    s.GlissTicks[v] = (p + 1) & 0xff;
                    s.Gliss[v] = NextParam(v);
                    break;
                case 0xc9:
                {
                    s.VibDelay[v] = p;
                    int cycle = NextParam(v);
                    s.VibCycle[v] = cycle;
                    int param = NextParam(v);
                    s.VibParam[v] = param;
                    s.VibMax[v] = CalcVibratoRate(param, cycle);
                    InitVibrato(v, param);
                    break;
                }
                case 0xca: s.VibParam[v] = 0; s.VibOffset[v] = 0; break;
                case 0xcb:
                {
                    s.TremDelay[v] = p;
                    int cycle = NextParam(v);
                    s.TremCycle[v] = cycle;
                    int param = NextParam(v);
                    s.TremParam[v] = param;
                    s.TremMax[v] = CalcVibratoRate(param, cycle);
                    InitTremolo(v, param);
                    break;
                }
                case 0xcc: s.TremParam[v] = 0; s.TremAcc[v] = 0; break;
                case 0xcd: PanSweep(v, p); break;
                case 0xce: s.SweepParam[v] = 0; s.SweepAcc[v] = 0; break;
                case 0xcf: s.NoiseClock = p & 0x1f; flgBuf = s.NoiseClock; break;
                case 0xd0: s.NoiseMask |= bit; break;
                case 0xd1: s.NoiseMask &= ~bit; break;
                case 0xd2: s.PmonMask |= bit; break;
                case 0xd3: s.PmonMask &= ~bit; break;
                case 0xd4: s.EchoMask |= bit; break;
                case 0xd5: s.EchoMask &= ~bit; break;
                case 0xd6: s.Octave[v] = p; break;
                case 0xd7: s.Octave[v] = (s.Octave[v] + 1) & 0xff; break;
                case 0xd8: s.Octave[v] = (s.Octave[v] - 1) & 0xff; break;
                case 0xd9: s.Transpose[v] = p; break;
                case 0xda: s.Transpose[v] = (s.Transpose[v] + p) & 0xff; break;
                case 0xdb: s.Detune[v] = p; break;
                case 0xdc:
                    s.Program[v] = p;
                    s.Tune0[v] = progTune0[p];
                    s.Tune1[v] = progTune1[p];
                    s.AdsrA[v] = progAdsr1[p];
                    s.AdsrB[v] = progAdsr2[p];
                    s.IgnoreVol &= ~bit;
                    if (p < 0x20 && dsp.Sources[p] == null) LastWarning = $"program {p:X2} (a game sound effect sample) in song {s.Id:X2}";
                    break;
                case 0xdd: s.AdsrA[v] = (s.AdsrA[v] & 0x70) | (p & 0x0f) | 0x80; SetAdsr(v); break;
                case 0xde: s.AdsrA[v] = (s.AdsrA[v] & 0x0f) | ((p & 7) << 4) | 0x80; SetAdsr(v); break;
                case 0xdf: s.AdsrB[v] = (s.AdsrB[v] & 0x1f) | ((p & 7) << 5); SetAdsr(v); break;
                case 0xe0: s.AdsrB[v] = (s.AdsrB[v] & 0xe0) | (p & 0x1f); SetAdsr(v); break;
                case 0xe1:
                    s.AdsrA[v] = progAdsr1[s.Program[v] & 0xff];
                    s.AdsrB[v] = progAdsr2[s.Program[v] & 0xff];
                    SetAdsr(v);
                    break;
                case 0xe2:
                {
                    int sp = (s.LoopSp[v] + 1) & 3;
                    s.LoopSp[v] = sp;
                    s.LoopLeft[v, sp] = p == 0 ? 0 : (p + 1) & 0xff;
                    s.LoopIter[v, sp] = 1;
                    s.LoopAddr[v, sp] = s.Ptr[v];
                    break;
                }
                case 0xe3:
                {
                    int sp = s.LoopSp[v];
                    s.LoopIter[v, sp] = (s.LoopIter[v, sp] + 1) & 0xff;
                    int left = s.LoopLeft[v, sp];
                    if (left != 0)
                    {
                        left = (left - 1) & 0xff;
                        if (left == 0) { s.LoopSp[v] = (sp - 1) & 3; break; }
                        s.LoopLeft[v, sp] = left;
                    }
                    s.Ptr[v] = s.LoopAddr[v, sp];
                    break;
                }
                case 0xe4: s.Slur |= bit; s.SlurOn &= ~bit; s.Roll &= ~bit; break;
                case 0xe6: s.Roll |= bit; s.Slur &= ~bit; break;
                case 0xe8: s.Dur[v] = p; break;
                case 0xe9:
                case 0xea:
                    LastWarning = $"song {s.Id:X2} voice {v} plays a game sound effect ({cmd:X2} {p:X2}); voice ended";
                    TerminateChan(v);
                    return false;
                case 0xf0: s.Tempo = p << 8; s.TempoCount = 0; break;
                case 0xf1:
                {
                    s.TempoCount = p;
                    int dur = p;
                    int target = NextParam(v);
                    if (dur == 0) { s.Tempo = target << 8; s.TempoCount = 0; break; }
                    int diff = target - (s.Tempo >> 8);
                    if ((diff & 0xff) == 0) { s.TempoCount = 0; break; }
                    s.TempoRate = CalcEnvRate(diff, diff >= 0, dur);
                    break;
                }
                case 0xf2: SetEchoVolume(p); break;
                case 0xf3:
                {
                    s.EchoVolCount = p;
                    int dur = p;
                    int target = NextParam(v);
                    if (dur == 0) { SetEchoVolume(target); break; }
                    int a = (Asr(target) ^ 0x80) & 0xff;
                    int cur = ((s.EchoVol >> 8) ^ 0x80) & 0xff;
                    int diff = a - cur;
                    if ((diff & 0xff) == 0) { s.EchoVolCount = 0; break; }
                    s.EchoVolRate = CalcEnvRate(diff, diff >= 0, dur);
                    break;
                }
                case 0xf4: s.SongVol = p; break;
                case 0xf5:
                {
                    int count = p;
                    int lo = NextParam(v), hi = NextParam(v);
                    int sp = s.LoopSp[v];
                    if (s.LoopIter[v, sp] != count) break;
                    if (((s.LoopLeft[v, sp] - 1) & 0xff) == 0) s.LoopSp[v] = (sp - 1) & 3;
                    s.Ptr[v] = ((lo | (hi << 8)) + s.Offset) & 0xffff;
                    break;
                }
                case 0xf6:
                {
                    int hi = NextParam(v);
                    s.Ptr[v] = ((p | (hi << 8)) + s.Offset) & 0xffff;
                    break;
                }
                case 0xf7:
                {
                    s.FeedbackCount = p;
                    int dur = p;
                    int target = NextParam(v);
                    if (dur == 0) { s.Feedback = (s.Feedback & 0xff) | (target << 8); break; }
                    int a = (target ^ 0x80) & 0xff;
                    int cur = ((s.Feedback >> 8) ^ 0x80) & 0xff;
                    int diff = a - cur;
                    s.FeedbackRate = (diff & 0xff) == 0 ? 0 : CalcEnvRate(diff, diff >= 0, dur);
                    break;
                }
                case 0xf8:
                {
                    s.FirCount = p;
                    int dur = p;
                    int set = NextParam(v) & 3;
                    for (int i = 0; i < 8; i++)
                    {
                        int coef = FilterTbl[set * 8 + i];
                        if (dur == 0) s.Fir[i] = (s.Fir[i] & 0xff) | (coef << 8);
                        else
                        {
                            s.Fir[i] &= 0xff00;
                            int diff = ((coef ^ 0x80) & 0xff) - (((s.Fir[i] >> 8) ^ 0x80) & 0xff);
                            s.FirRate[i] = (diff & 0xff) == 0 ? 0 : CalcEnvRate(diff, diff >= 0, dur);
                        }
                    }
                    break;
                }
                case 0xf9: s.OutputCode = (s.OutputCode + 1) & 0xff; break;
                case 0xfa: s.OutputCode = 0; break;
                case 0xfb: s.IgnoreVol |= bit; break;
                case 0xfc:
                {
                    int hi = NextParam(v);
                    if ((condJump & bit) != 0)
                    {
                        condJump &= ~bit;
                        s.Ptr[v] = ((p | (hi << 8)) + s.Offset) & 0xffff;
                    }
                    break;
                }
                case 0xe5:
                case 0xe7:
                    break;                            // only the lookahead acts on these
                default:                              // $EB-$EF, $FD-$FF: end of this voice's script
                    TerminateChan(v);
                    return false;
            }
            return true;
        }

        static int Asr(int b) => ((b >> 1) | (b & 0x80)) & 0xff;

        void SetVolume(int v, int p)
        {
            s.Vol[v] = (p & 0x7f) << 8;
            dirtyVol |= 1 << v;
            s.VolCount[v] = 0;
        }

        void SetPan(int v, int p)
        {
            s.Pan[v] = ((p << 1) & 0xff) << 8;
            dirtyVol |= 1 << v;
            s.PanCount[v] = 0;
        }

        void SetEchoVolume(int p)
        {
            s.EchoVol = Asr(p) << 8;
            s.EchoVolCount = 0;
        }

        void PanSweep(int v, int p1)
        {
            int a = (p1 + 1) & 0xff;
            bool carry = false;
            if (a == 0) { carry = true; }
            else { a &= 0xfe; if (a == 0) a = 2; }
            s.SweepCycle[v] = a;
            int half = (a >> 1) | (carry ? 0x80 : 0);
            s.SweepCount[v] = half;
            int p2 = NextParam(v);
            bool neg = (p2 & 0x80) != 0;
            int amp = p2 & 0x7f;
            if (neg) amp ^= 0x7f;
            int rate;
            if ((half & 0x80) != 0) rate = amp;
            else
            {
                int q1 = amp / half, r1 = amp % half;
                int q0 = (r1 << 8) / half;
                rate = ((q1 & 0xff) << 8) | (q0 & 0xff);
                if (rate == 0) rate = 1;
            }
            if (neg) rate = (-rate) & 0xffff;
            s.SweepRate[v] = rate;
            s.SweepParam[v] = p2;
            s.SweepAcc[v] = 0;
        }

        static int CalcVibratoRate(int param, int cycle)
        {
            int amp = (param & 0x3f) + 1;
            int r;
            if (cycle == 0) r = amp;
            else
            {
                int q1 = amp / cycle, r1 = amp % cycle;
                int q0 = (r1 << 8) / cycle;
                r = ((q1 & 0xff) << 8) | (q0 & 0xff);
            }
            r >>= 2;
            if (r == 0) r = 1;
            return (r << 2) & 0xffff;
        }

        // the initial rate (a quarter of the full rate during a fade-in, negated for the
        // "negative" shape) and the fade-in step count (7 when there is a delay)
        static int CalcVibratoDelay(int delay, int saved, ref int rate, int param)
        {
            if (delay != 0) rate = (rate & 0xffff) >> 2;
            if (param >= 0x80 && param < 0xc0) rate = (-rate) & 0xffff;
            return delay != 0 ? saved | 7 : saved;
        }

        void InitVibrato(int v, int param)
        {
            Cycles += Cost.EffectInit;
            s.VibOffset[v] = 0;
            s.VibAcc[v] = 0;
            s.VibCount[v] = s.VibCycle[v];
            int rate = s.VibMax[v];
            int saved = s.Gain[v] & 0x70;
            s.VibDelayCount[v] = s.VibDelay[v];
            s.Gain[v] = CalcVibratoDelay(s.VibDelay[v], saved, ref rate, param);
            s.VibRate[v] = rate;
        }

        // (as the driver has it: the tremolo's fade-in steps land in the vibrato's bits)
        void InitTremolo(int v, int param)
        {
            Cycles += Cost.EffectInit;
            s.TremAcc[v] = 0;
            s.TremCount[v] = s.TremCycle[v];
            int rate = s.TremMax[v];
            int saved = s.Gain[v] & 0x70;
            s.TremDelayCount[v] = s.TremDelay[v];
            s.Gain[v] = CalcVibratoDelay(s.TremDelay[v], saved, ref rate, param);
            s.TremRate[v] = rate;
        }

        // one half-cycle of a vibrato/tremolo: turn round, growing by a quarter of the full rate on
        // the way up while fade-in steps are left
        static int UpdateVibratoTremolo(int gain, int max, int param, int prev, out int newGain)
        {
            newGain = 0;
            int c = prev & 0xffff;
            if (param >= 0xc0)
            {
                if (gain == 0) return (-c) & 0xffff;
                newGain = gain - 1;
                bool wasNeg = (c & 0x8000) != 0;
                if (wasNeg) c = (-c) & 0xffff;
                c = (((max & 0xffff) >> 2) + c) & 0xffff;
                return wasNeg ? c : (-c) & 0xffff;
            }
            if (param >= 0x80)
            {
                c = (-c) & 0xffff;
                if (gain == 0) return c;
                newGain = gain - 1;
                if ((c & 0x8000) == 0) return c;
                int q = (-((max & 0xffff) >> 2)) & 0xffff;
                return (q + c) & 0xffff;
            }
            c = (-c) & 0xffff;
            if (gain == 0) return c;
            newGain = gain - 1;
            if ((c & 0x8000) != 0) return c;
            return (((max & 0xffff) >> 2) + c) & 0xffff;
        }

        void UpdateChanEnv(int v)
        {
            int bit = 1 << v;
            if (s.VibDelayCount[v] != 0) s.VibDelayCount[v]--;
            if (s.TremDelayCount[v] != 0) s.TremDelayCount[v]--;
            if (s.VolCount[v] != 0)
            {
                s.VolCount[v]--;
                int before = s.Vol[v] >> 8;
                s.Vol[v] = (s.Vol[v] + s.VolRate[v]) & 0xffff;
                if ((s.Vol[v] >> 8) != before) dirtyVol |= bit;
            }
            if (s.PanCount[v] != 0)
            {
                s.PanCount[v]--;
                int before = s.Pan[v] >> 8;
                s.Pan[v] = (s.Pan[v] + s.PanRate[v]) & 0xffff;
                if ((s.Pan[v] >> 8) != before) dirtyVol |= bit;
            }
            int rate = s.GlissRate[v], rateLo = s.GlissRateLo[v];
            if (rate != 0 || rateLo != 0)
            {
                int left = (s.GlissTicks[v] - 1) & 0xff;
                if (left == 0) { s.GlissRate[v] = 0; s.GlissRateLo[v] = 0; }
                s.GlissTicks[v] = left;
                int acc = ((s.Freq[v] & 0xffff) << 8) | (s.GlissFrac[v] & 0xff);
                acc = (acc + ((rate << 8) | rateLo)) & 0xffffff;
                s.Freq[v] = acc >> 8;
                s.GlissFrac[v] = acc & 0xff;
                dirtyFreq |= bit;
            }
            if (s.SweepParam[v] != 0)
            {
                int before = s.SweepAcc[v] >> 8;
                s.SweepAcc[v] = (s.SweepAcc[v] + s.SweepRate[v]) & 0xffff;
                if ((s.SweepAcc[v] >> 8) != before) dirtyVol |= bit;
                int c = (s.SweepCount[v] - 1) & 0xff;
                if (c == 0)
                {
                    s.SweepRate[v] = (-s.SweepRate[v]) & 0xffff;
                    c = s.SweepCycle[v];
                }
                s.SweepCount[v] = c;
            }
        }

        void UpdateFreqVol(int v)
        {
            int bit = 1 << v;
            Cycles += Cost.FreqVol;
            int vp = s.VibParam[v];
            if (vp != 0 && s.VibDelayCount[v] == 0)
            {
                Cycles += Cost.Effect;
                int rate = s.VibRate[v];
                int before = s.VibAcc[v] >> 8;
                int acc = (s.VibAcc[v] + rate) & 0xffff;
                s.VibAcc[v] = acc;
                if ((acc >> 8) != before)
                {
                    Cycles += Cost.VibChange;
                    int c = (acc << 1) & 0xffff;
                    int f = s.Freq[v];
                    int vv = (((f >> 8) * 15) + (((f & 0xff) * 15) >> 8)) & 0xffff;
                    int vh = vv >> 8, vl = vv & 0xff, ch = c >> 8, cl = c & 0xff;
                    int o = (vh * ch) & 0xffff;
                    o = (o + ((vl * ch) >> 8)) & 0xffff;
                    o = (o + ((vh * cl) >> 8)) & 0xffff;
                    if ((c & 0x8000) != 0) o = (o - vv) & 0xffff;
                    if (o != s.VibOffset[v]) { s.VibOffset[v] = o; dirtyFreq |= bit; }
                }
                int cnt = (s.VibCount[v] - 1) & 0xff;
                if (cnt == 0)
                {
                    Cycles += Cost.EffectTurn;
                    int g = s.Gain[v];
                    s.VibRate[v] = UpdateVibratoTremolo(g & 7, s.VibMax[v], vp, rate, out int ng);
                    s.Gain[v] = ng | (g & 0x70);
                    cnt = s.VibCycle[v];
                }
                s.VibCount[v] = cnt;
            }
            int tp = s.TremParam[v];
            if (tp != 0 && s.TremDelayCount[v] == 0)
            {
                Cycles += Cost.Effect;
                int rate = s.TremRate[v];
                int before = s.TremAcc[v] >> 8;
                s.TremAcc[v] = (s.TremAcc[v] + rate) & 0xffff;
                if ((s.TremAcc[v] >> 8) != before) { dirtyVol |= bit; Cycles += Cost.TremChange; }
                int cnt = (s.TremCount[v] - 1) & 0xff;
                if (cnt == 0)
                {
                    Cycles += Cost.EffectTurn;
                    int g = s.Gain[v];
                    s.TremRate[v] = UpdateVibratoTremolo((g >> 4) & 7, s.TremMax[v], tp, rate, out int ng);
                    s.Gain[v] = (ng << 4) | (g & 7);
                    cnt = s.TremCycle[v];
                }
                s.TremCount[v] = cnt;
            }
            if ((dirtyVol & bit) != 0) { dirtyVol &= ~bit; UpdateChanVol(v); Cycles += Cost.VolUpdate; }
            if ((dirtyFreq & bit) != 0) { dirtyFreq &= ~bit; UpdateChanFreq(v); Cycles += Cost.FreqUpdate; }
        }

        static int AddToPan(int add, int pan)
        {
            int r = add + pan;
            if ((add & 0x80) == 0) return r > 0xff ? 0xff : r;
            return r > 0xff ? r & 0xff : 0;
        }

        void UpdateChanVol(int v)
        {
            int bit = 1 << v;
            int pan = AddToPan(s.SweepAcc[v] >> 8, s.Pan[v] >> 8);
            int factor = pan ^ 0xff;
            int vol = s.Vol[v] >> 8;
            int trem = s.TremAcc[v] >> 8;
            if (trem != 0)
            {
                int prod = vol * ((trem << 1) & 0xff);
                if ((trem & 0x80) != 0) vol = prod >> 8;
                else
                {
                    int a = (prod >> 8) + vol;
                    vol = (a & 0x80) != 0 ? 0x7f : a;
                }
            }
            int y = (vol * (masterVol >> 8)) >> 8;
            if ((s.IgnoreVol & bit) == 0) y = (y * s.SongVol) >> 8;
            int l = (factor * y) >> 8;
            int r = (((factor ^ 0xff) & 0xff) * y) >> 8;
            dsp.Write((v << 4) | SnesDsp.VOLL, l);
            dsp.Write((v << 4) | SnesDsp.VOLR, r);
        }

        void UpdateChanFreq(int v)
        {
            int f = (s.Freq[v] + s.VibOffset[v]) & 0xffff;
            int ratio = pitchRatio;
            int prod = ((ratio * (f >> 8)) & 0xffff) + ((ratio * (f & 0xff)) >> 8);
            prod &= 0xffff;
            if ((ratio & 0x80) != 0) f = prod;
            else f = ((prod << 1) + f) & 0xffff;
            dsp.Write((v << 4) | SnesDsp.PITCHL, f & 0xff);
            dsp.Write((v << 4) | SnesDsp.PITCHH, f >> 8);
        }

        void UpdateSample(int v)
        {
            dsp.Write((v << 4) | SnesDsp.SRCN, s.Program[v]);
            SetAdsr(v);
        }

        void SetAdsr(int v)
        {
            Cycles += Cost.Adsr;
            dsp.Write((v << 4) | SnesDsp.ADSR1, s.AdsrA[v]);
            dsp.Write((v << 4) | SnesDsp.ADSR2, s.AdsrB[v]);
        }

        void InitChan(State n, int v)
        {
            n.LoopSp[v] = 0;
            n.Gliss[v] = 0;
            n.GlissRate[v] = 0;
            n.GlissRateLo[v] = 0;
            n.VibParam[v] = 0;
            n.TremParam[v] = 0;
            n.SweepParam[v] = 0;
            n.SweepAcc[v] = 0;
            n.Detune[v] = 0;
            n.Transpose[v] = 0;
            n.Dur[v] = 1;
        }

        void TerminateChan(int v)
        {
            int bit = 1 << v;
            s.Active &= ~bit;
            s.NoiseMask &= ~bit;
            s.PmonMask &= ~bit;
            s.EchoMask &= ~bit;
        }
    }

}
