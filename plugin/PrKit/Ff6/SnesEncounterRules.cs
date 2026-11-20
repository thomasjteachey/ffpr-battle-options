using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PrKit.Ff6
{
    // FF6's random battles as the SNES counts them (field/battle.asm CheckBattleWorld / CheckBattleSub),
    // without the game: SnesEncounters.cs is the hook that runs them on the PR's steps. One danger
    // counter ($1F6E, kept in the save) gains a rate on every step where battles can happen (at most
    // $FF00); a battle comes when a random roll is below its high byte, and the counter starts again from
    // 0. The rate comes from two 16-word tables (C0/C29F world maps, C0/C2BF other maps) indexed by the
    // party's encounter relics x 4 + the place's 2-bit rate code: relics bit 0 the Charm Bangle (vanilla:
    // half), bit 1 the Moogle Charm (0); codes on other maps (2 bits per SNES map, CF/5880) normal $70,
    // low $40, high $160, very high $200; on world maps (2 bits per 32x32-tile zone and terrain group,
    // CF/5800 World of Balance, CF/5840 World of Ruin; a zone's groups at bits 7-6 .. 1-0) normal $C0,
    // low $60, high $180, none 0.
    //
    // A mod's rules are this class with its numbers: vanilla's below (Worlds Collide keeps them); Brave
    // New World's from its ROM (tools/port_encounter_rates.py: its codes, and a battle roll of
    // 4 + random x 233 / 256, its "at least about 10 steps between battles"), read by Parse from the mod's
    // data. Where it has them, the rules also say each place's code (world zone codes, PR ground -> SNES
    // terrain group, PR monster set -> code); a mod may instead say it itself (SnesEncounterMod.RateCode).
    public sealed class SnesEncounterRules
    {
        public const int MaxDanger = 0xFF00;
        public const int NoBattles = -1;   // a RateCode: no battles here (rate 0)

        // C0/C29F and C0/C2BF: [relics x 4 + code] (vanilla's)
        public int[] WorldRates = { 0xC0, 0x60, 0x180, 0, 0x60, 0x30, 0xC0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        public int[] MapRates = { 0x70, 0x40, 0x160, 0x200, 0x38, 0x20, 0xB0, 0x100, 0, 0, 0, 0, 0, 0, 0, 0 };
        // the battle roll (C0/C479): RollAdd + random 0-255 x RollRange / 256 (vanilla: the random byte itself)
        public int RollAdd = 0, RollRange = 256;

        // where the mod's data names the codes (null: the mod's RateCode says)
        public byte[][] WorldZones;                 // [world 1 WoB / 2 WoR][zone 0-63]: the zone's 4 group codes
        public Dictionary<int, int>[] FootGroup;    // [world][PR ground type (foot_information)] -> terrain group 0-3
        public Dictionary<int, int> SetCode;        // other maps: PR monster set -> code (NoBattles: none)
        public string Name = "vanilla";

        public static SnesEncounterRules Vanilla => new SnesEncounterRules();

        // the roll for a random byte 0-255
        public int Roll(int rand8) => RollRange >= 256 && RollAdd == 0 ? rand8 & 0xFF : RollAdd + ((rand8 & 0xFF) * RollRange >> 8);

        // one step: the new danger, and whether a battle comes (roll: Roll's result)
        public static bool Advance(int danger, int rate, int roll, out int after)
        {
            after = Math.Min(Math.Clamp(danger, 0, MaxDanger) + Math.Max(0, rate), MaxDanger);
            return rate > 0 && roll < after >> 8;
        }

        // the rate for a code (NoBattles or out of range: 0) with the party's relics
        public int Rate(bool world, int code, bool bangle, bool moogle)
        {
            if (code < 0 || code > 3) return 0;
            var t = world ? WorldRates : MapRates;
            int i = (moogle ? 8 : 0) + (bangle ? 4 : 0) + code;
            return i < t.Length ? t[i] : 0;
        }

        // a world map's code from the zone's byte and the ground's terrain group (no table: -2)
        public int WorldCode(int world, int zone, int foot)
        {
            if (WorldZones == null || world < 1 || world >= WorldZones.Length || WorldZones[world] == null) return -2;
            var z = WorldZones[world];
            if (zone < 0 || zone >= z.Length) return 0;
            int g = 0;
            if (FootGroup != null && world < FootGroup.Length && FootGroup[world] != null) FootGroup[world].TryGetValue(foot, out g);
            return z[zone] >> (2 * (3 - (g & 3))) & 3;
        }

        // another map's code from its PR monster set (no table: -2)
        public int MapCode(int set) => SetCode == null ? -2 : SetCode.TryGetValue(set, out int c) ? c : 0;

        // The chance a step ends in a battle at this danger, and the mean steps from 0 to a battle at a
        // rate (offline checks: tests, the log)
        public double BattleChance(int danger)
        {
            int hi = Math.Clamp(danger, 0, MaxDanger) >> 8, n = 0;
            for (int r = 0; r < 256; r++) if (Roll(r) < hi) n++;
            return n / 256.0;
        }

        public double MeanSteps(int rate)
        {
            if (rate <= 0) return double.PositiveInfinity;
            double alive = 1, mean = 0;
            int danger = 0;
            for (int n = 1; alive > 1e-12 && n < 100000; n++)
            {
                danger = Math.Min(danger + rate, MaxDanger);
                double p = BattleChance(danger);
                mean += n * alive * p;
                alive *= 1 - p;
            }
            return mean;
        }

        // the mod's data ({"name", "world_rates", "map_rates", "roll": {"add", "range"}, "world_zones":
        // {"1": [64], "2": [64]}, "foot_group": {"1": {"foot": group}}, "set_codes": {"set": code}})
        public static SnesEncounterRules Parse(JsonElement e)
        {
            var r = new SnesEncounterRules();
            if (e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) r.Name = n.GetString();
            if (e.TryGetProperty("world_rates", out var wr)) r.WorldRates = Ints(wr, 16);
            if (e.TryGetProperty("map_rates", out var mr)) r.MapRates = Ints(mr, 16);
            if (e.TryGetProperty("roll", out var roll))
            {
                if (roll.TryGetProperty("add", out var a)) r.RollAdd = a.GetInt32();
                if (roll.TryGetProperty("range", out var g)) r.RollRange = g.GetInt32();
            }
            if (e.TryGetProperty("world_zones", out var wz) && wz.ValueKind == JsonValueKind.Object)
            {
                r.WorldZones = new byte[3][];
                foreach (var p in wz.EnumerateObject())
                    if (int.TryParse(p.Name, out int w) && w >= 1 && w <= 2)
                    {
                        var z = Ints(p.Value, 64);
                        r.WorldZones[w] = Array.ConvertAll(z, v => (byte)v);
                    }
            }
            if (e.TryGetProperty("foot_group", out var fg) && fg.ValueKind == JsonValueKind.Object)
            {
                r.FootGroup = new Dictionary<int, int>[3];
                foreach (var p in fg.EnumerateObject())
                    if (int.TryParse(p.Name, out int w) && w >= 1 && w <= 2)
                        r.FootGroup[w] = IntMap(p.Value);
            }
            if (e.TryGetProperty("set_codes", out var sc) && sc.ValueKind == JsonValueKind.Object) r.SetCode = IntMap(sc);
            return r;
        }

        static int[] Ints(JsonElement a, int count)
        {
            var o = new int[count];
            int i = 0;
            foreach (var v in a.EnumerateArray())
            {
                if (i >= count) break;
                o[i++] = v.GetInt32();
            }
            if (i != count) throw new FormatException($"{count} numbers wanted, {i} found");
            return o;
        }

        static Dictionary<int, int> IntMap(JsonElement o)
        {
            var d = new Dictionary<int, int>();
            foreach (var p in o.EnumerateObject())
                if (int.TryParse(p.Name, out int k)) d[k] = p.Value.GetInt32();
            return d;
        }
    }
}
