using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace PrKit.Snes
{
    // Plays a SongPack: FF6's driver on the S-DSP, rendered on its own thread to a waveOut stream
    // (or pulled with Render() by an offline renderer). Commands come from any thread and take
    // effect at the next render; fades and the game's volume are applied here, after the chip.
    //
    //   var player = new SnesMusicPlayer(SongPack.Load(dir));
    //   player.Start();                       // the output stream
    //   player.Play(0x24, fadeIn: 0);          // the battle theme slot
    //   player.Play(0x2f, suspend: true);      // a song that keeps the one it interrupts...
    //   player.Play(0x24);                     // ...and asking for that one again resumes it
    //   player.Stop(fadeOut: 1.5f);
    //   player.Volume = 0.5f;                  // master x BGM, 0..1
    public sealed class SnesMusicPlayer : IDisposable
    {
        public const int Rate = SnesDsp.SampleRate;

        public SongPack Pack { get; private set; }
        readonly SnesDsp dsp = new SnesDsp();
        readonly Ff6Driver driver;
        readonly ConcurrentQueue<Action> commands = new ConcurrentQueue<Action>();
        // the DSP's output between two driver ticks
        readonly short[] seg = new short[8192 * 2];
        int segLen, segPos;
        long sampleNow, nextRound;
        const int TickCycles = 39 * 128;          // timer 0: 39 steps of 128 SPC700 cycles = 156 samples
        WaveOutStream output;

        // Schedule ticks as the console does when its sound CPU overruns a tick (Ff6Driver.Cycles);
        // off: exactly every 4.875 ms.
        public bool ModelCpuTime = true;
        public long LateTicks;                    // ticks the console would have run late
        public long LostTicks;                    // timer steps it would have missed

        // the S-DSP's main volume is FF6's $3F (half scale): this brings songs up to about the
        // loudness of the game's own recordings
        public float Loudness = 2.0f;
        public volatile float Volume = 0.5f;
        float fade = 1f, fadeTarget = 1f, fadeStep;
        bool stopWhenFaded;
        bool playing;

        // stats (read from any thread)
        public long FramesRendered;
        public double RenderSeconds;          // CPU time spent rendering
        public int PeakLevel, RmsLevel;       // last ~0.5 s, 0..32767 after gain
        public int RawPeak, RawRms;           // the same from the chip, before fades and volume
        long levelSq, rawSq; int levelPeak, rawPeak, levelCount;
        public int Underruns => output?.Underruns ?? 0;
        public string OutputError => output?.Error;
        public int CurrentSlot { get; private set; } = -1;
        public int PausedSlot => driver.PausedId;
        public bool IsPlaying => playing;
        public int OutputCode => driver.OutputCode;
        public string LastWarning => driver.LastWarning;
        public int ActiveVoices => dsp.ActiveVoices;

        public SnesMusicPlayer(SongPack pack)
        {
            driver = new Ff6Driver(dsp);
            SetPack(pack);
        }

        public void SetPack(SongPack pack) => commands.Enqueue(() =>
        {
            Pack = pack;
            if (pack == null) { driver.Stop(); playing = false; CurrentSlot = -1; return; }
            driver.LookaheadPatch = pack.LookaheadPatch;
            driver.SetResident(pack.Resident);
            driver.SetEchoDelay(pack.EchoDelay);
        });

        // Open the output stream (32 kHz stereo). bufferFrames * bufferCount is the latency. silent:
        // no sound card (test runs): the music is rendered at its real pace and thrown away, so levels,
        // timing and CPU load can still be checked.
        public bool Start(int bufferFrames = 1024, int bufferCount = 4, bool silent = false)
        {
            if (output != null || sink != null) return output?.Error == null;
            if (silent)
            {
                sink = new System.Threading.Thread(() =>
                {
                    var b = new short[bufferFrames * 2];
                    var clock = Stopwatch.StartNew();
                    long done = 0;
                    while (!disposed)
                    {
                        if (!paused && done < (long)(clock.Elapsed.TotalSeconds * Rate) + bufferFrames)
                        {
                            Render(b, bufferFrames);
                            done += bufferFrames;
                        }
                        else System.Threading.Thread.Sleep(5);
                        if (paused) done = (long)(clock.Elapsed.TotalSeconds * Rate);
                    }
                }) { IsBackground = true, Name = "PrKit SNES audio (silent)" };
                sink.Start();
                return true;
            }
            output = new WaveOutStream(Rate, bufferFrames, bufferCount, Render);
            return output.Error == null;
        }

        System.Threading.Thread sink;
        volatile bool paused, disposed;
        public bool Silent => sink != null;

        public bool Paused
        {
            get => paused;
            set { paused = value; if (output != null) output.Paused = value; }
        }

        public bool HasSong(int slot) => Pack != null && Pack.Songs.ContainsKey(slot);

        // Play a slot. suspend: keep the current song to resume when it is asked for again (FF6's
        // battle songs); a fade-in starts from silence.
        public void Play(int slot, float fadeIn = 0, bool suspend = false, int volume = 0xff) => commands.Enqueue(() =>
        {
            if (Pack == null || !Pack.Songs.TryGetValue(slot, out var song)) return;
            FinishPendingSuspend();
            if (slot == CurrentSlot && playing && !stopWhenFaded) { FadeTo(1f, fadeIn); return; }
            driver.Play(slot, song, volume, false, suspend);
            CurrentSlot = slot;
            playing = true;
            stopWhenFaded = false;
            if (fadeIn > 0) { fade = 0; FadeTo(1f, fadeIn); }
            else { fade = fadeTarget = 1f; fadeStep = 0; }
        });

        public void Stop(float fadeOut = 0) => commands.Enqueue(() =>
        {
            FinishPendingSuspend();
            if (!playing) return;
            if (fadeOut > 0) { FadeTo(0f, fadeOut); stopWhenFaded = true; }
            else StopNow();
        });

        // Keep the playing song to Resume() later (a battle starting over the field's song), fading out.
        public void Suspend(float fadeOut = 0) => commands.Enqueue(() =>
        {
            FinishPendingSuspend();
            if (!playing) return;
            if (fadeOut > 0) { FadeTo(0f, fadeOut); suspendWhenFaded = true; }
            else SuspendNow();
        });

        // The suspended song again, where it was (the driver's own fade-in, as after a battle).
        public void Resume() => commands.Enqueue(() =>
        {
            FinishPendingSuspend();
            if (stopWhenFaded) StopNow();
            int id = driver.PausedId;
            if (!driver.ResumePaused()) return;
            CurrentSlot = id;
            playing = true;
            fade = fadeTarget = 1f;
            fadeStep = 0;
        });

        public void DropPaused() => commands.Enqueue(() => driver.DropPaused());

        // Let the song's conditional jumps go (FF6's Dancing Mad moves to its next part this way).
        public void ConditionalJump() => commands.Enqueue(() => driver.EnableConditionalJump());

        bool suspendWhenFaded;

        void FinishPendingSuspend()
        {
            if (suspendWhenFaded) SuspendNow();
        }

        void SuspendNow()
        {
            driver.SuspendCurrent();
            playing = false;
            suspendWhenFaded = false;
            stopWhenFaded = false;
            CurrentSlot = -1;
            fade = fadeTarget = 1f;
            fadeStep = 0;
        }

        // Fade the music (not the game's volume setting) to a level, 0..1.
        public void Fade(float level, float seconds) => commands.Enqueue(() => FadeTo(level, seconds));

        public void Tempo(int ratio) => commands.Enqueue(() => driver.SetTempoRatio(ratio));
        public void Pitch(int ratio) => commands.Enqueue(() => driver.SetPitchRatio(ratio));

        void FadeTo(float level, float seconds)
        {
            fadeTarget = level;
            fadeStep = seconds <= 0 ? 0 : (level - fade) / (seconds * Rate);
            if (fadeStep == 0) fade = level;
        }

        void StopNow()
        {
            driver.Stop(keepPaused: true);
            playing = false;
            stopWhenFaded = false;
            CurrentSlot = -1;
            fade = fadeTarget = 1f;
            fadeStep = 0;
        }

        // Render `frames` stereo frames (the output thread calls this; so can a test).
        public void Render(short[] buf, int frames)
        {
            long t0 = Stopwatch.GetTimestamp();
            while (commands.TryDequeue(out var c)) c();
            float vol = Volume * Loudness;
            for (int f = 0; f < frames; f++)
            {
                if (segPos >= segLen)
                {
                    long start = Math.Max(nextRound, sampleNow * 32);
                    driver.Tick();
                    long next;
                    if (!ModelCpuTime) next = start + TickCycles;
                    else
                    {
                        long end = start + (long)(driver.Cycles * Ff6Driver.Cost.Scale);
                        long steps = end / TickCycles - start / TickCycles;
                        if (steps >= 1)
                        {
                            next = end;               // the timer stepped meanwhile: straight on
                            LateTicks++;
                            LostTicks += steps - 1;
                        }
                        else next = (start / TickCycles + 1) * TickCycles;
                    }
                    nextRound = next;
                    segLen = (int)Math.Max(1, Math.Min(8192, (next + 31) / 32 - sampleNow));
                    dsp.Render(seg, 0, segLen);
                    sampleNow += segLen;
                    segPos = 0;
                }
                if (fadeStep != 0)
                {
                    fade += fadeStep;
                    if ((fadeStep > 0 && fade >= fadeTarget) || (fadeStep < 0 && fade <= fadeTarget))
                    {
                        fade = fadeTarget;
                        fadeStep = 0;
                        if (stopWhenFaded) StopNow();
                        else if (suspendWhenFaded) SuspendNow();
                    }
                }
                float g = playing ? vol * fade : 0f;
                int sl = seg[segPos * 2], sr = seg[segPos * 2 + 1];
                int l = (int)(sl * g), r = (int)(sr * g);
                int ra = Math.Max(Math.Abs(sl), Math.Abs(sr));
                if (ra > rawPeak) rawPeak = ra;
                rawSq += (long)sl * sl + (long)sr * sr;
                segPos++;
                l = l > 32767 ? 32767 : l < -32768 ? -32768 : l;
                r = r > 32767 ? 32767 : r < -32768 ? -32768 : r;
                buf[f * 2] = (short)l;
                buf[f * 2 + 1] = (short)r;
                int a = Math.Max(Math.Abs(l), Math.Abs(r));
                if (a > levelPeak) levelPeak = a;
                levelSq += (long)l * l + (long)r * r;
                if (++levelCount >= Rate / 2)
                {
                    PeakLevel = levelPeak;
                    RmsLevel = (int)Math.Sqrt(levelSq / (2.0 * levelCount));
                    RawPeak = rawPeak;
                    RawRms = (int)Math.Sqrt(rawSq / (2.0 * levelCount));
                    levelPeak = 0; levelSq = 0; rawPeak = 0; rawSq = 0; levelCount = 0;
                }
            }
            FramesRendered += frames;
            RenderSeconds += (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
        }

        // CPU time per second of music, as a share of one core.
        public double Load => FramesRendered == 0 ? 0 : RenderSeconds / (FramesRendered / (double)Rate);

        public void Dispose()
        {
            disposed = true;
            output?.Dispose();
            output = null;
        }
    }
}
