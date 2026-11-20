using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PrKit.Ff6
{
    // A mod's SNES monster AI as the porter exports it (tools/port_ai.py snes_ai_data): SNES monster id ->
    // its AI script (main and counter sections), its Battle / special abilities; SNES attack index -> PR
    // ability; battle lines; the formations' slots. Worlds Collide ships it in each seed (wc_seed.json),
    // Brave New World in bnw_data.json; SnesAiHost runs the scripts with it.
    public sealed class SnesAiData
    {
        public int Version;
        public SnesAiRules Rules = SnesAiRules.Vanilla;
        public Dictionary<int, byte[]> Scripts = new Dictionary<int, byte[]>();
        public Dictionary<int, int> Battle = new Dictionary<int, int>(), Special = new Dictionary<int, int>();
        public int[] Attacks = Array.Empty<int>(), Items = Array.Empty<int>();
        public Dictionary<int, string[]> Messages = new Dictionary<int, string[]>();      // own text (no PR line)
        public Dictionary<int, string[]> MessageKeys = new Dictionary<int, string[]>();   // the PR's own lines
        public HashSet<int> Plugin = new HashSet<int>();   // SNES ids whose turns the plugin runs (the shim)
        public Dictionary<int, int[]> PartySlots = new Dictionary<int, int[]>();   // party -> SNES slot of cells 1-9 (-1 none)
        public Dictionary<int, int> Copies = new Dictionary<int, int>();   // PR monster copy -> the SNES monster it stands for
        // the mod's items and commands as PR abilities (port_ai.item_abilities): SNES item -> ability, command -> ability
        public Dictionary<int, int> ItemUse = new Dictionary<int, int>(), Throws = new Dictionary<int, int>(), Commands = new Dictionary<int, int>();
        // party -> each SNES slot's member: {SNES id, cell, shown at start (1/0), the PR party lists it (1/0)}, or null
        public Dictionary<int, int[][]> PartyMembers = new Dictionary<int, int[][]>();
        public Dictionary<int, int> Weak = new Dictionary<int, int>();          // SNES monster -> weak element bits
        public Dictionary<int, int> Formations = new Dictionary<int, int>();    // PR party -> SNES formation
        // monsters HP loss never kills (the rules' BossDeathByScript: BNW's boss flag): their script does
        public HashSet<int> BossDeath = new HashSet<int>();

        public static byte[] Hex(string h)
        {
            h ??= "";
            var bytes = new byte[h.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(h.Substring(2 * i, 2), 16);
            return bytes;
        }

        // the snes_ai object of a mod's data file
        public static SnesAiData Parse(JsonElement sai)
        {
            var d = new SnesAiData();
            static void Ints(JsonElement o, string key, Dictionary<int, int> into)
            {
                if (o.TryGetProperty(key, out var m) && m.ValueKind == JsonValueKind.Object)
                    foreach (var p in m.EnumerateObject()) into[int.Parse(p.Name)] = p.Value.GetInt32();
            }
            static void Lines(JsonElement o, string key, Dictionary<int, string[]> into)
            {
                if (o.TryGetProperty(key, out var m) && m.ValueKind == JsonValueKind.Object)
                    foreach (var p in m.EnumerateObject()) into[int.Parse(p.Name)] = p.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            }
            if (sai.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number) d.Version = v.GetInt32();
            foreach (var p in sai.GetProperty("scripts").EnumerateObject()) d.Scripts[int.Parse(p.Name)] = Hex(p.Value.GetString());
            Ints(sai, "battle", d.Battle);
            Ints(sai, "special", d.Special);
            d.Attacks = sai.GetProperty("attacks").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (sai.TryGetProperty("items", out var its)) d.Items = its.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Ints(sai, "weak", d.Weak);
            Ints(sai, "formations", d.Formations);
            if (sai.TryGetProperty("party_members", out var pm))
                foreach (var p in pm.EnumerateObject())
                    d.PartyMembers[int.Parse(p.Name)] = p.Value.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.Array ? x.EnumerateArray().Select(n => n.GetInt32()).ToArray() : null).ToArray();
            Ints(sai, "item_use", d.ItemUse);
            Ints(sai, "throws", d.Throws);
            Ints(sai, "commands", d.Commands);
            Ints(sai, "copies", d.Copies);
            if (sai.TryGetProperty("party_slots", out var ps))
                foreach (var p in ps.EnumerateObject()) d.PartySlots[int.Parse(p.Name)] = p.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (sai.TryGetProperty("plugin", out var pl))
                foreach (var x in pl.EnumerateArray()) d.Plugin.Add(x.GetInt32());
            Lines(sai, "message_keys", d.MessageKeys);
            Lines(sai, "messages", d.Messages);
            // the mod's engine: its rules by name, its own attack -> command table (C2/1DD8) if it changed it
            if (sai.TryGetProperty("rules", out var r) && r.ValueKind == JsonValueKind.String) d.Rules = SnesAiRules.ByName(r.GetString());
            if (sai.TryGetProperty("command_table", out var ct) && ct.ValueKind == JsonValueKind.Array)
            {
                var t = ct.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (t.Length % 2 == 0 && t.Length > 0) d.Rules = d.Rules.WithCommandTable(t.Take(t.Length / 2).ToArray(), t.Skip(t.Length / 2).ToArray());
            }
            if (sai.TryGetProperty("boss_death", out var bd))
                foreach (var x in bd.EnumerateArray()) d.BossDeath.Add(x.GetInt32());
            return d;
        }
    }
}
