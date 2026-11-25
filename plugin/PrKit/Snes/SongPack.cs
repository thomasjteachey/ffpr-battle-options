using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PrKit.Snes
{
    // A set of SNES songs for FF6's driver, as a randomizer's seed (or a mod's data) writes them:
    //
    //   <dir>/songs.json   {"format": "prkit-snes-songs/1", "driver": "ff6", "lookahead_patch": true,
    //                       "echo_delay": 5, "blob": "songs.bin",
    //                       "samples": [{"id": 1, "offset": 0, "length": 9*n, "loop": 0, "tune": [t0, t1],
    //                                    "adsr": [a1, a2], "name": "..."}, ...],
    //                       "songs": [{"slot": 1, "name": "...", "offset": ..., "length": ...,
    //                                  "instruments": [16 sample ids, 0 = none], "title": "..."}, ...]}
    //   <dir>/songs.bin    the BRR blocks (no length header) and the song scripts as the driver keeps
    //                      them at $1C00 (header + script), at the offsets songs.json gives
    //
    // tools/snes_songs.py writes and checks the format; tools/bc_music.py fills it from a Beyond
    // Chaos seed. Slots are the game's song ids; what plays where is the mod's business.
    public sealed class SongPack
    {
        public string Dir;
        public bool LookaheadPatch = true;
        public int EchoDelay = 5;
        public readonly Dictionary<int, Ff6Song> Songs = new Dictionary<int, Ff6Song>();
        public readonly Dictionary<int, string> Titles = new Dictionary<int, string>();
        public int SampleCount;
        // programs $00-$07, FF6's resident waveforms, when the pack carries them ("resident" rows:
        // tools/snes_songs.py resident / add_resident); null: the player's synthesized stand-ins
        public readonly Ff6Instrument[] Resident = new Ff6Instrument[8];
        public long Bytes;

        public static SongPack Load(string dir)
        {
            var pack = new SongPack { Dir = dir };
            string json = File.ReadAllText(Path.Combine(dir, "songs.json"));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string blobName = root.TryGetProperty("blob", out var b) ? b.GetString() : "songs.bin";
            byte[] blob = File.ReadAllBytes(Path.Combine(dir, blobName));
            pack.Bytes = blob.Length;
            if (root.TryGetProperty("lookahead_patch", out var lp)) pack.LookaheadPatch = lp.GetBoolean();
            if (root.TryGetProperty("echo_delay", out var ed)) pack.EchoDelay = ed.GetInt32();

            var samples = new Dictionary<int, Ff6Instrument>();
            foreach (var e in root.GetProperty("samples").EnumerateArray())
            {
                int id = e.GetProperty("id").GetInt32();
                int off = e.GetProperty("offset").GetInt32(), len = e.GetProperty("length").GetInt32();
                var data = new byte[len];
                Array.Copy(blob, off, data, 0, len);
                var tune = e.GetProperty("tune");
                var adsr = e.GetProperty("adsr");
                string name = e.TryGetProperty("name", out var n) ? n.GetString() : null;
                samples[id] = new Ff6Instrument
                {
                    Sample = new BrrSample(data, e.GetProperty("loop").GetInt32(), name),
                    Tune0 = (byte)tune[0].GetInt32(), Tune1 = (byte)tune[1].GetInt32(),
                    Adsr1 = (byte)adsr[0].GetInt32(), Adsr2 = (byte)adsr[1].GetInt32(),
                };
            }
            pack.SampleCount = samples.Count;
            if (root.TryGetProperty("resident", out var res))
                foreach (var e in res.EnumerateArray())
                {
                    int prog = e.GetProperty("program").GetInt32();
                    if (prog < 0 || prog > 7) continue;
                    int off = e.GetProperty("offset").GetInt32(), len = e.GetProperty("length").GetInt32();
                    var data = new byte[len];
                    Array.Copy(blob, off, data, 0, len);
                    var tune = e.GetProperty("tune");
                    var adsr = e.GetProperty("adsr");
                    pack.Resident[prog] = new Ff6Instrument
                    {
                        Sample = new BrrSample(data, e.GetProperty("loop").GetInt32(), $"resident {prog}"),
                        Tune0 = (byte)tune[0].GetInt32(), Tune1 = (byte)tune[1].GetInt32(),
                        Adsr1 = (byte)adsr[0].GetInt32(), Adsr2 = (byte)adsr[1].GetInt32(),
                    };
                }

            foreach (var e in root.GetProperty("songs").EnumerateArray())
            {
                int slot = e.GetProperty("slot").GetInt32();
                int off = e.GetProperty("offset").GetInt32(), len = e.GetProperty("length").GetInt32();
                var song = new Ff6Song { Data = new byte[len] };
                Array.Copy(blob, off, song.Data, 0, len);
                song.Name = e.TryGetProperty("name", out var n) ? n.GetString() : $"song {slot:X2}";
                int i = 0;
                foreach (var id in e.GetProperty("instruments").EnumerateArray())
                {
                    if (i >= 16) break;
                    int sid = id.GetInt32();
                    song.Instruments[i++] = sid != 0 && samples.TryGetValue(sid, out var ins) ? ins : null;
                }
                pack.Songs[slot] = song;
                if (e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                    pack.Titles[slot] = t.GetString();
            }
            return pack;
        }
    }
}
