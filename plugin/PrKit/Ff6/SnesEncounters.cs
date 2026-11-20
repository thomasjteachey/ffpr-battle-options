using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Data.Master;
using Last.Data.User;
using Last.Entity.Field;
using Last.Management;
using Last.Map;

namespace PrKit.Ff6
{
    // Where a step goes, as a mod's rate table sees it
    public struct SnesEncounterStep
    {
        public int MapId;   // the PR map (UserDataManager.CurrentMapId)
        public int World;   // 1 World of Balance, 2 World of Ruin (the map's EncountAreaGridId); 0 another map
        public int Zone;    // world maps: the 32x32-tile zone (y / 32 x 8 + x / 32) where the world map was last loaded
        public int Set;     // the cell's PR monster set (0: none)
        public int Foot;    // the cell's PR ground (foot_information type; 0: unknown)
    }

    // What a mod tells the encounter hook: its rules (rate tables, roll), each place's rate code, its
    // encounter relics, its log tag.
    public abstract class SnesEncounterMod
    {
        public virtual SnesEncounterRules Rules => SnesEncounterRules.Vanilla;
        public virtual bool Active => true;
        public virtual string Tag => "kit";
        // the place's rate code (SnesEncounterRules: 0-3, or NoBattles); by default the rules' own tables
        public virtual int RateCode(in SnesEncounterStep step)
        {
            var r = Rules;
            int c = step.World > 0 ? r.WorldCode(step.World, step.Zone, step.Foot) : r.MapCode(step.Set);
            return c == -2 ? 0 : c;
        }
        // the party's encounter relics: null = the game's own (the PR's Charm Bangle counts a step at its
        // value / 1.5, the hook reads that; its Moogle Charm stops the game's check)
        public virtual bool? HalfRate() => null;
        public virtual bool NoEncounters() => false;
        public virtual void Warn(string key, Exception e) => Hooks.WarnOnce($"{Tag} {key}", e);
        public virtual long PerfStart() => 0;
        public virtual void PerfLap(long t0) { }
    }

    // The SNES's random battles in place of the PR's step counter. Written for Worlds Collide (the user,
    // 2026-11-11: "can you ensure the encounter rate is on parity with the WC snes"; WcPlugin/WcEncounters.cs,
    // 2026-11-11), in the kit since 2026-11-20 so Brave New World has its own rates (the user: "is BNW mod
    // using the SNES encounter rate?"). A mod calls SnesEncounters.Patch(harmony, its SnesEncounterMod) once.
    // SNES: SnesEncounterRules. Mean steps per battle (vanilla): world 23 (low 34, high 16), other maps 31
    // (low 41, high 17). A world map's zone is the one where the world map was last loaded (entered, or back
    // from a battle): the SNES doesn't re-read it as the party walks.
    // PR (decompiled EncounterLot.CheckEncount, tools/decomp/encounter, preencount): a move counter
    // gains the ground's subtract_steps (100; 160 on two grounds; Charm Bangle / 1.5) per step; past a
    // threshold of 2625-4375 (drawn after each battle) each step draws 1-1000 against the set's
    // cumulative weights 80/80/80/16 (256), so a party comes 25.6% of the time: ~38 steps everywhere.
    // Here, around the step's one call of CheckEncount (FootMonitoring.ImmediatelyBeforeMoveingAction:
    // CheckEncount itself can't be patched or called, its "in List<OwnedCharacterData>" parameter makes
    // Harmony's wrapper an invalid program and the type doesn't load for a call): the PR's threshold is
    // kept out of reach and its counter holds the SNES's danger (so a save keeps it, as the SNES's
    // does); each step is rolled the SNES's way before the game's check; a battle puts the game's
    // counter just under its threshold and draws the party 1-256 against the set's weights (vanilla's
    // 80/80/80/16: the SNES's 5/16, 5/16, 5/16, 1/16; a mod's own odds are its sets' weights, BNW's
    // 80/80/48/48), so the game's own check (a cell with encounters, not the Moogle Charm, no encounter
    // pending) lets it happen and the game picks the Veldt's fights, the background and the start (back
    // attacks...) and preloads the battle as it does. Only steps the game counts change the danger; its
    // re-roll after a battle (Shuffle: also drops the preloaded battle) keeps it but after a random
    // battle. Tested headless (WcTest encounters): a battle the game sets up from a step is called off
    // there, as the encounter's start can't run without graphics (the PR's own random battles stop a
    // -nographics game at that point too).
    public static class SnesEncounters
    {
        const int Unreachable = 70000;  // the PR's threshold, past the danger's $FF00 + a step
        static readonly Random Rng = new Random();
        static SnesEncounterMod Mod;
        static int zone = -1, zoneMap = -1;
        static bool zoneStale = true;
        public static int Steps, Battles;
        public static string Last = "";
        public static bool Paused;    // tests: no battles (the PR's threshold still out of reach)
        public static bool Off;       // tests: the PR's own encounters, untouched
        // tests: a battle the game set up is called off again (the encounter's start can't run headless:
        // the PR's own random battles stop a -nographics game there too); counted in SetUp
        public static bool CancelForTest;
        public static int SetUp;

        static SnesEncounterRules R => Mod?.Rules ?? SnesEncounterRules.Vanilla;
        static string Tag => Mod?.Tag ?? "kit";

        public static void Patch(Harmony h, SnesEncounterMod mod)
        {
            Mod = mod;
            // CheckEncount itself can't be patched or called from managed code: its "in
            // List<OwnedCharacterData>" parameter makes Harmony's wrapper an invalid program (the
            // party froze after 4 steps in the 2026-11-11 test) and the type doesn't load for a call.
            // Its one caller, FootMonitoring.ImmediatelyBeforeMoveingAction(entity), runs it once a step.
            var m = AccessTools.Method(typeof(FootMonitoring), "ImmediatelyBeforeMoveingAction");
            if (m == null) throw new MissingMethodException("FootMonitoring.ImmediatelyBeforeMoveingAction");
            encountMonitor = AccessTools.Property(typeof(FootMonitoring), "encountMonitor");
            mapAccessor = AccessTools.Property(typeof(FootMonitoring), "mapAccessor");
            if (encountMonitor == null || mapAccessor == null)
                throw new MissingMemberException($"FootMonitoring.encountMonitor {encountMonitor != null}, mapAccessor {mapAccessor != null}");
            h.Patch(m, prefix: new HarmonyMethod(typeof(SnesEncounters), nameof(Step_Prefix)),
                       postfix: new HarmonyMethod(typeof(SnesEncounters), nameof(Step_Postfix)));
            // the party: the PR draws 1-1000 against its sets' cumulative weights (80/80/80/16 = 256),
            // so past its threshold a step finds a party 25.6% of the time and tries again on the next
            // (EncountTable.GetFractionNumber, decompiled tools/decomp/preencount). On a step the SNES's
            // counter chose, the draw is 1-256 (UpdateBattleGrpRng's random byte against the weights).
            var fraction = AccessTools.Method(typeof(EncounterLot), "RandomFraction");
            if (fraction == null) throw new MissingMethodException("EncounterLot.RandomFraction");
            h.Patch(fraction, postfix: new HarmonyMethod(typeof(SnesEncounters), nameof(RandomFraction_Postfix)));
            var shuffle = AccessTools.Method(typeof(EncounterLot), "Shuffle");
            if (shuffle == null) throw new MissingMethodException("EncounterLot.Shuffle");
            h.Patch(shuffle, prefix: new HarmonyMethod(typeof(SnesEncounters), nameof(Shuffle_Prefix)),
                             postfix: new HarmonyMethod(typeof(SnesEncounters), nameof(Shuffle_Postfix)));
            // the game harness (test runs): the counter's state; "cancel on" calls off a battle a step sets
            // up (a headless game can't start one), "paused on" rolls no battles, "off on" the game's own counter
            Harness.Register("encounters", a =>
            {
                for (int i = 0; i + 1 < a.Length; i += 2)
                {
                    bool on = a[i + 1] == "on";
                    if (a[i] == "cancel") CancelForTest = on;
                    else if (a[i] == "paused") Paused = on;
                    else if (a[i] == "off") Off = on;
                }
                return $"encounters: {Tag} ({R.Name} rules) steps {Steps}, battles {Battles}, set up {SetUp}; cancel {CancelForTest}, " +
                       $"paused {Paused}, off {Off}; relics: half {Mod?.HalfRate()?.ToString() ?? "game's"}, none {Mod?.NoEncounters()}; last {Last}";
            }, "encounters [cancel|paused|off on|off]...: the SNES danger counter (steps, battles, the last step); cancel on: a battle a step sets up is called off");
            var r = R;
            Kit.Info($"[{Tag}] encounters: the SNES's danger counter hooked ({r.Name} rules: mean steps world {r.MeanSteps(r.WorldRates[0]):0.#}, " +
                     $"other maps {r.MeanSteps(r.MapRates[0]):0.#}{(r.RollRange < 256 || r.RollAdd > 0 ? $"; roll {r.RollAdd}-{r.RollAdd + r.RollRange - 1}" : "")})");
        }

        static System.Reflection.PropertyInfo encountMonitor, mapAccessor;

        static EncounterLot LotOf(FootMonitoring foot) =>
            (encountMonitor?.GetValue(foot) as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<EncounterLot>();

        // the field (FieldController: the step caller's map accessor), kept while the step caller and
        // the map stay the same. A step used to look it up with FindObjectOfType<FieldMap>(), a search
        // of every loaded object: walking stuttered on the Deck (the user, 2026-11-11: "only when walking
        // on the field. battles seem fine").
        static IntPtr fieldFoot;
        static int fieldMap = -1, gridMap = -1, grid;
        static FieldController field;

        static FieldController FieldOf(FootMonitoring foot, int mapId)
        {
            if (field == null || fieldFoot != foot.Pointer || fieldMap != mapId)
            {
                field = (mapAccessor?.GetValue(foot) as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<FieldController>();
                fieldFoot = foot.Pointer;
                fieldMap = mapId;
            }
            return field;
        }

        static int GridOf(int mapId)
        {
            if (gridMap != mapId)
            {
                grid = MasterManager.Instance?.GetData<Map>(mapId)?.EncountAreaGridId ?? 0;
                gridMap = mapId;
            }
            return grid;
        }

        static bool Active => Mod != null && Mod.Active;

        // this step's plan (CheckEncount runs on the main thread, one step at a time)
        static int before, next, planRate, planRoll, planGround;
        static bool planned, battle, lastBangle, randomBattle, planBangleKnown;
        static string planWhere = "";

        // the SNES's danger counter for one step: the new value, and whether a battle comes
        public static bool Advance(int danger, int rate, int roll, out int after) => SnesEncounterRules.Advance(danger, rate, roll, out after);

        // the mod's battle roll for this step
        public static int Roll() => R.Roll(Rng.Next(256));

        // before the game's own check: the SNES's roll for this step, as if the game counts it. A
        // battle: the game's counter 0 under a threshold of 1, so this step's value passes it and the
        // game itself picks the party and starts the fight; else the threshold out of reach. A step the
        // game doesn't count (no battles on the cell, a pending encounter) changes nothing.
        static EncounterLot planLot;

        public static void Step_Prefix(FootMonitoring __instance, FieldEntity __0)
        {
            planned = false;
            if (!Active || Off) return;
            long t0 = Mod.PerfStart();
            try
            {
                var lot = LotOf(__instance);
                if (lot == null || !lot.Enable || lot.IsReady()) return;   // no encounters here / one pending
                int mapId = UserDataManager.Instance()?.CurrentMapId ?? 0;
                var fc = FieldOf(__instance, mapId);
                if (fc == null) return;
                planLot = lot;
                before = Math.Clamp(lot.GetMoveCount(), 0, SnesEncounterRules.MaxDanger);
                bool? half = Mod.HalfRate();
                planBangleKnown = half.HasValue;
                planRate = RateHere(fc, mapId, __0, half ?? lastBangle, out planWhere, out planGround, Mod.NoEncounters());
                planRoll = Roll();
                battle = Advance(before, planRate, planRoll, out next) && !Paused;
                if (battle) lot.SetRestoreData(0, 1);
                else lot.SetRestoreData(before, Unreachable);
                planned = true;
            }
            catch (Exception e) { Mod.Warn("encounters-pre", e); }
            finally { Mod.PerfLap(t0); }
        }

        public static void RandomFraction_Postfix(EncountTable __0, ref int __result)
        {
            if (!planned || !battle || __0 == null || __result >= 0) return;
            try { __result = __0.GetFractionNumber(1 + Rng.Next(256)); }
            catch (Exception e) { Mod.Warn("encounters-party", e); }
        }

        // the game's re-roll after every battle (and its own resets; it also drops the battle it
        // preloaded, so it runs): the SNES's counter only starts again from 0 when a random battle
        // comes (event battles, map changes and the rest keep it)
        static int shuffleDanger = -1;

        public static void Shuffle_Prefix(EncounterLot __instance)
        {
            shuffleDanger = -1;
            if (!Active || Off) return;
            try { shuffleDanger = randomBattle ? 0 : Math.Clamp(__instance.GetMoveCount(), 0, SnesEncounterRules.MaxDanger); }
            catch (Exception e) { Mod.Warn("encounters-shuffle", e); }
        }

        public static void Shuffle_Postfix(EncounterLot __instance)
        {
            if (shuffleDanger < 0) return;
            try { __instance.SetRestoreData(shuffleDanger, Unreachable); }
            catch (Exception e) { Mod.Warn("encounters-shuffle", e); }
            randomBattle = false;
            shuffleDanger = -1;
        }

        public static void Step_Postfix()
        {
            if (!planned) return;
            planned = false;
            var lot = planLot;
            planLot = null;
            long t0 = Mod.PerfStart();
            try
            {
                int counted = lot.GetMoveCount() - (battle ? 0 : before);
                if (counted <= 0) { lot.SetRestoreData(before, Unreachable); return; }   // not a step here
                bool encounter = lot.IsReady();   // CheckEncount's yes: the encounter it set up
                if (!planBangleKnown) lastBangle = planGround > 1 && counted < planGround;   // the PR's Charm Bangle: its value / 1.5
                Steps++;
                Last = $"{planWhere}: +{planRate:X} -> {next:X4}, roll {planRoll}";
                if (!battle)
                {
                    lot.SetRestoreData(Paused ? before : next, Unreachable);
                    return;
                }
                // the SNES's counter starts again from 0 (and from 0 again at the game's re-roll after it)
                if (!encounter) lot.SetRestoreData(0, Unreachable);
                randomBattle = encounter;
                zoneStale = true;   // the SNES reloads the world map after a battle
                Battles++;
                Last += encounter ? " -> battle" : " -> no party here";
                Kit.Info($"[{Tag}] encounters: {Last} (step {Steps})");
                if (encounter && CancelForTest)
                {
                    SetUp++;
                    lot.ClearEncountData();
                    lot.SetRestoreData(0, Unreachable);
                    randomBattle = false;
                    Kit.Info($"[{Tag}] encounters: the test called that battle off");
                }
            }
            catch (Exception e) { Mod.Warn("encounters", e); }
            finally { Mod.PerfLap(t0); }
        }

        // the SNES's rate for a step onto this entity's destination (bangle: the Charm Bangle's half,
        // as the game counted the last step or as the mod's relics say; moogle: the mod's no-encounter
        // relic; ground: the game's own value for this cell)
        public static int RateHere(FieldController fc, int mapId, FieldEntity entity, bool bangle, out string where, out int ground, bool moogle = false)
        {
            var info = fc?.Last_Map_IMapEncounter_GetEncountInfo(entity, true);
            var step = new SnesEncounterStep { MapId = mapId, Set = info?.Table?.MonsterSetId ?? 0 };
            ground = 1;
            if (info != null && info.FootInfo.HasValue)
            {
                ground = info.FootInfo.Value.SubtrackStep;
                step.Foot = info.FootInfo.Value.TypeId;
            }
            int g = GridOf(mapId);
            string relics = (bangle ? " bangle" : "") + (moogle ? " moogle" : "");
            if (g == 1 || g == 2)
            {
                if (zoneStale || zoneMap != mapId || zone < 0)
                {
                    var c = fc.ConvertWorldPositionToCellPosition(entity.transform.position);
                    int x = Math.Clamp(UnityEngine.Mathf.RoundToInt(c.x), 0, 255), y = Math.Clamp(UnityEngine.Mathf.RoundToInt(c.y), 0, 255);
                    zone = y / 32 * 8 + x / 32;
                    zoneMap = mapId;
                    zoneStale = false;
                }
                step.World = g;
                step.Zone = zone;
                int wc = Mod?.RateCode(step) ?? 0;
                where = $"map {mapId} zone {zone} set {step.Set} ground {step.Foot} code {wc}{relics}";
                return R.Rate(true, wc, bangle, moogle);
            }
            int code = Mod?.RateCode(step) ?? 0;
            where = $"map {mapId} set {step.Set} code {code}{relics}";
            return R.Rate(false, code, bangle, moogle);
        }
    }
}
