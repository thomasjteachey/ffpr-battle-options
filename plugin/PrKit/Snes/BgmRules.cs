using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace PrKit.Snes
{
    // What plays where: which of a mod's SNES songs (song pack slots) replaces which of the game's tracks.
    // Game-free (offline tests); SnesBgm applies it in game. Read from a mod's bgm.json:
    //
    //   {"tracks":      {"<PR bgm id>": slot, ...},           the game's track -> the pack's slot (pr_to_slot too)
    //    "songs":       {"<slot hex>": {"name": "...", "title": "...", "pr_bgm": id or null}, ...}
    //                   pr_bgm: this slot holds a song the game has itself: the game plays its own track id
    //    "maps":        {"<PR map id>": {"from": [PR bgm ids], "slot": s}, ...}   an area's own song (s -1: none)
    //    "battles":     {"<monster party id>": {"slot": s}, ...}  s -1: keep the field song (formations too)
    //    "fanfares":    {"<monster party id>": {"slot": s}, ...}  that battle's victory fanfare (the game's
    //                                                             "fanfare_track", default 7) -> s; -1: none
    //    "jumps":       [[PR bgm id, slot], ...]   asked for while that slot plays: its next part (the song's
    //                                              conditional jumps; FF6's Dancing Mad parts 2 and 3)
    //    "carrier":     PR bgm id the game plays (silent) behind a song whose own PR id has no track,
    //    "pause_songs": [slot, ...]  songs that keep the song they interrupt (FF6's driver's pause list: the
    //                                field song resumes after them; after any other it starts over). Empty or
    //                                absent: every song keeps it (the PR's own way)}
    //
    // Slots and ids are the mod's: FF6's SNES song ids for a ported FF6 mod (tools/snes_songs.py has FF6's
    // song -> PR track table), any numbers for new songs.
    public sealed class BgmRules
    {
        public sealed class MapRule { public HashSet<int> From = new HashSet<int>(); public int Slot = -1; }
        public sealed class Song { public string Name, Title; public int? GameTrack; }

        public readonly Dictionary<int, int> Tracks = new Dictionary<int, int>();
        public readonly Dictionary<int, Song> Songs = new Dictionary<int, Song>();
        public readonly Dictionary<int, MapRule> Maps = new Dictionary<int, MapRule>();
        public readonly Dictionary<int, int> Battles = new Dictionary<int, int>();
        public readonly Dictionary<int, int> Fanfares = new Dictionary<int, int>();
        public int FanfareTrack = 7;
        public readonly HashSet<(int pr, int slot)> Jumps = new HashSet<(int, int)>();
        public int Carrier = -1;
        public readonly HashSet<int> PauseSongs = new HashSet<int>();

        public enum Kind { Game, Ours, GameTrack, Jump, Silent }

        public struct Choice
        {
            public Kind Kind;
            public int Slot;          // the pack's slot (Ours, Jump, GameTrack), -1 none
            public int PrBgm;         // what the game plays: the request, or the game's own track for a GameTrack
            public string Why;
            public override string ToString() => $"{Kind} slot {(Slot >= 0 ? Slot.ToString("X2") : "-")} PR {PrBgm} ({Why})";
        }

        public string SongName(int slot) => Songs.TryGetValue(slot, out var s) ? s.Name : null;

        // The choice for a BGM request: the game's track prBgm on channel (0 field, 1 battle) on map mapId;
        // battleSlot: the battle's own song when this is a battle's request (int.MinValue: none); playing:
        // the slot the player is on for this channel (-1 none); party: the last battle's monster party (-1 none)
        // for its victory fanfare.
        public Choice Resolve(int prBgm, int channel, int mapId, int battleSlot, int playing, Func<int, bool> hasSong, int party = -1)
        {
            int slot;
            string why;
            if (battleSlot != int.MinValue) { slot = battleSlot; why = "battle"; }
            else if (party >= 0 && prBgm == FanfareTrack && Fanfares.TryGetValue(party, out slot))
            {
                why = $"party {party}'s fanfare";
                if (slot < 0) return new Choice { Kind = Kind.Silent, Slot = -1, PrBgm = prBgm, Why = why + ": none" };
            }
            else if (Maps.TryGetValue(mapId, out var mr) && mr.From.Contains(prBgm) && mr.Slot >= 0) { slot = mr.Slot; why = $"map {mapId}"; }
            else if (Tracks.TryGetValue(prBgm, out slot)) why = "track";
            else return new Choice { Kind = Kind.Game, Slot = -1, PrBgm = prBgm, Why = "no song for it" };
            if (Jumps.Contains((prBgm, slot)) && playing == slot)
                return new Choice { Kind = Kind.Jump, Slot = slot, PrBgm = prBgm, Why = "the song's next part" };
            if (Songs.TryGetValue(slot, out var s) && s.GameTrack != null)
                return new Choice { Kind = Kind.GameTrack, Slot = slot, PrBgm = s.GameTrack.Value, Why = why + ", the game's own song" };
            if (hasSong != null && !hasSong(slot))
                return new Choice { Kind = Kind.Game, Slot = -1, PrBgm = prBgm, Why = $"{why}: slot {slot:X2} not in the pack" };
            return new Choice { Kind = Kind.Ours, Slot = slot, PrBgm = prBgm, Why = why };
        }

        public static BgmRules Load(string json)
        {
            var r = new BgmRules();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var name in new[] { "tracks", "pr_to_slot" })
                if (root.TryGetProperty(name, out var t))
                    foreach (var e in t.EnumerateObject()) r.Tracks[int.Parse(e.Name, CultureInfo.InvariantCulture)] = e.Value.GetInt32();
            if (root.TryGetProperty("songs", out var songs))
                foreach (var e in songs.EnumerateObject())
                {
                    int slot = Convert.ToInt32(e.Name, 16);
                    r.Songs[slot] = new Song
                    {
                        Name = Str(e.Value, "name"), Title = Str(e.Value, "title"),
                        GameTrack = e.Value.TryGetProperty("pr_bgm", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : (int?)null,
                    };
                }
            if (root.TryGetProperty("maps", out var maps))
                foreach (var e in maps.EnumerateObject())
                {
                    var m = new MapRule { Slot = e.Value.GetProperty("slot").GetInt32() };
                    foreach (var f in e.Value.GetProperty("from").EnumerateArray()) m.From.Add(f.GetInt32());
                    r.Maps[int.Parse(e.Name, CultureInfo.InvariantCulture)] = m;
                }
            foreach (var name in new[] { "battles", "formations", "fanfares" })
                if (root.TryGetProperty(name, out var b))
                    foreach (var e in b.EnumerateObject())
                        (name == "fanfares" ? r.Fanfares : r.Battles)[int.Parse(e.Name, CultureInfo.InvariantCulture)] =
                            e.Value.ValueKind == JsonValueKind.Number ? e.Value.GetInt32() : e.Value.GetProperty("slot").GetInt32();
            if (root.TryGetProperty("fanfare_track", out var ft) && ft.ValueKind == JsonValueKind.Number) r.FanfareTrack = ft.GetInt32();
            if (root.TryGetProperty("jumps", out var j))
                foreach (var e in j.EnumerateArray()) r.Jumps.Add((e[0].GetInt32(), e[1].GetInt32()));
            if (root.TryGetProperty("carrier", out var c) && c.ValueKind == JsonValueKind.Number) r.Carrier = c.GetInt32();
            if (root.TryGetProperty("pause_songs", out var ps))
                foreach (var e in ps.EnumerateArray()) r.PauseSongs.Add(e.GetInt32());
            return r;
        }

        static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
