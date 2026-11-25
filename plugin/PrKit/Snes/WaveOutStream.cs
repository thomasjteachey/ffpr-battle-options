using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PrKit.Snes
{
    // A PCM stream to the sound card through winmm's waveOut (Windows, and Wine/Proton on the Steam
    // Deck). The Pixel Remasters play their audio through their own native driver and ship with
    // Unity's audio switched off, so a plugin that makes its own sound needs its own stream. A
    // background thread keeps a few buffers queued, asking `fill` for each.
    public sealed class WaveOutStream : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormatEx
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int device, ref WaveFormatEx fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        const int WAVE_MAPPER = -1, WHDR_DONE = 1;
        // WAVEHDR (x64): lpData 0, dwBufferLength 8, dwBytesRecorded 12, dwUser 16, dwFlags 24,
        // dwLoops 28, lpNext 32, reserved 40
        const int HdrSize = 48, FlagsOffset = 24;

        readonly IntPtr handle;
        readonly IntPtr[] headers, datas;
        readonly bool[] queued;
        readonly short[] staging;
        readonly int frames;
        readonly Action<short[], int> fill;
        readonly Thread thread;
        volatile bool running = true, paused;

        public int Underruns;
        public int Rate { get; }
        public string Error { get; private set; }

        // fill(buffer, frames): write `frames` interleaved stereo samples
        public WaveOutStream(int rate, int bufferFrames, int bufferCount, Action<short[], int> fill)
        {
            Rate = rate;
            frames = bufferFrames;
            this.fill = fill;
            var fmt = new WaveFormatEx
            {
                wFormatTag = 1, nChannels = 2, nSamplesPerSec = (uint)rate, wBitsPerSample = 16,
                nBlockAlign = 4, nAvgBytesPerSec = (uint)rate * 4, cbSize = 0,
            };
            int r = waveOutOpen(out handle, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) { Error = $"waveOutOpen failed ({r})"; running = false; return; }
            headers = new IntPtr[bufferCount];
            datas = new IntPtr[bufferCount];
            queued = new bool[bufferCount];
            staging = new short[bufferFrames * 2];
            for (int i = 0; i < bufferCount; i++)
            {
                datas[i] = Marshal.AllocHGlobal(bufferFrames * 4);
                headers[i] = Marshal.AllocHGlobal(HdrSize);
                for (int b = 0; b < HdrSize; b += 8) Marshal.WriteInt64(headers[i], b, 0);
                Marshal.WriteIntPtr(headers[i], 0, datas[i]);
                Marshal.WriteInt32(headers[i], 8, bufferFrames * 4);
                waveOutPrepareHeader(handle, headers[i], HdrSize);
            }
            thread = new Thread(Run) { IsBackground = true, Name = "PrKit SNES audio", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        void Run()
        {
            try
            {
                while (running)
                {
                    if (paused) { Thread.Sleep(10); continue; }
                    int done = 0, fed = 0;
                    for (int i = 0; i < headers.Length; i++)
                    {
                        bool free = !queued[i] || (Marshal.ReadInt32(headers[i], FlagsOffset) & WHDR_DONE) != 0;
                        if (!free) continue;
                        if (queued[i]) done++;
                        fill(staging, frames);
                        Marshal.Copy(staging, 0, datas[i], frames * 2);
                        int flags = Marshal.ReadInt32(headers[i], FlagsOffset) & ~WHDR_DONE;
                        Marshal.WriteInt32(headers[i], FlagsOffset, flags);
                        waveOutWrite(handle, headers[i], HdrSize);
                        queued[i] = true;
                        fed++;
                    }
                    if (done == headers.Length) Underruns++;
                    Thread.Sleep(fed > 0 ? 1 : 4);
                }
            }
            catch (Exception e)
            {
                Error = e.GetType().Name + ": " + e.Message;
            }
        }

        public bool Paused
        {
            get => paused;
            set
            {
                if (paused == value || handle == IntPtr.Zero) return;
                paused = value;
                if (value) waveOutPause(handle); else waveOutRestart(handle);
            }
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            running = false;
            thread?.Join(500);
            waveOutReset(handle);
            for (int i = 0; i < headers.Length; i++)
            {
                waveOutUnprepareHeader(handle, headers[i], HdrSize);
                Marshal.FreeHGlobal(headers[i]);
                Marshal.FreeHGlobal(datas[i]);
            }
            waveOutClose(handle);
        }
    }
}
