using System;
using System.Collections.Generic;
using System.Linq;
using Last.Battle;
using UnityEngine;

namespace PrKit.Ff6
{
    // What a mod tells the host: its data (null: off), its log tag, and the few things mods do their own way.
    public abstract class SnesAiMod
    {
        public abstract SnesAiData Data { get; }
        public virtual bool Active => Data != null;
        public virtual string Tag => "kit";              // log lines "[tag] enemy AI ..."; own battle lines TAG_BTL_xx_n
        public virtual bool Verbose => TestRun.Running;   // a line per turn, counter, death and entry
        // SNES attack index -> PR ability for this monster (Worlds Collide: its ability scaling first)
        public virtual int AbilityFor(int snesMonster, int attack) => SnesAiHost.BaseAbility(Data, snesMonster, attack);
        // the party whose layout and formation a battle uses (Worlds Collide: an event battle's look source)
        public virtual int PartySource(int party) => party;
        // a battle line of the mod's own text, shown under a key of its own
        public virtual void SetText(string key, string text) => GameText.Set(key, text);
        // FB 0B / 0C (set / clear a status on itself; 0B 07 = it dies) and FB 02 (the battle ends): BNW's
        // boss deaths use them (Worlds Collide leaves them off until its fights are tested with them)
        public virtual bool ScriptStatusEffects => true;
    }

    // Every monster runs its mod's SNES AI script the way the SNES runs it: the mod's data carries each
    // monster's script (main and counter sections, tools/port_ai.py snes_ai_data), SnesAi.cs is FF6's
    // interpreter, and this host is the battle it looks at plus the hooks that turn its actions into the
    // PR's acts. Written for Worlds Collide (the user, 2026-11-12: "please make all enemies/bosses behave
    // like WC SNES"; WcPlugin/WcEnemyAi*.cs, 2026-11-12..18); in the kit since 2026-11-19 so Brave New
    // World runs its own scripts too (the user: "Start putting BNW's monsters on the kit's AI interpreter").
    // A mod calls SnesAiHost.Patch(harmony, its SnesAiMod) once and SnesAiHost.Update() every frame.
    //
    // This part: the battle as the interpreter sees it (ISnesAiBattle). Units 0-3 are the party slots
    // (BattlePlayerData.corpsIndex), 4-9 the monster slots in the formation's order; FF6's battle timer
    // and monster timers tick every 16 frames of battle time ($3A44, MonsterTimer: C2/5BFC); variables
    // $00-$23 are the battle's, shared by its monsters (each monster has its own byte for $24 and up).
    public static partial class SnesAiHost
    {
        const double SnesFps = 60.0988, TickSeconds = 16 / SnesFps;
        internal static SnesAiMod Mod;

        // SNES status bit (status 1-4, 0-31) -> PR ConditionType (-1: none; NearFatal is HP-based)
        static readonly int[] StatusCondition =
        {
            9, 34, 10, 607, 604, 601, 11, 5,           // Dark, Zombie, Poison, Magitek, Clear, Imp, Petrify, Wound
            404, -2, 14, 6, 410, 12, 407, 7,            // Condemned, Near Fatal, Image, Mute, Berserk, Muddle, Sap, Sleep
            619, 25, 16, 15, 17, 412, 19, 413,          // Dance, Regen, Slow, Haste, Stop, Shell, Safe, Reflect
            616, 603, 602, 621, 421, 416, -1, 409,      // Rage, Frozen, Reraise, Morph, Chant, Hidden, Interceptor, Float
        };

        internal sealed class Fight
        {
            public readonly Dictionary<int, SnesAiMonster> Monsters = new Dictionary<int, SnesAiMonster>();  // by UniqueId
            public readonly Dictionary<int, int> Slot = new Dictionary<int, int>();       // UniqueId -> SNES slot 0-5
            public readonly Dictionary<int, int> SnesId = new Dictionary<int, int>();     // UniqueId -> SNES monster id
            public readonly byte[] Vars = new byte[0x24];
            public readonly double[] MonsterTime = new double[6];
            public double BattleTime;   // the scripts' battle timer (FC 16, FB 04 resets it)
            // the host's own clock for its waits (holds, deferred entries, arrivals, bosses at 0 HP): never
            // reset. They ran on BattleTime until 2026-11-20, so a monster's timed attack (if timer >= n,
            // FB 04 reset, attack: ~110 of BNW's scripts, vanilla's pattern too) pushed them back each time
            public double Clock;
            public int Formation = -1;
            public int[] CellSlots;   // the data's SNES slot of each party cell (null: by cell order)
            public int[][] Members;   // the data's member of each SNES slot: {SNES id, cell, shown at start, listed} (null: unknown)
            public readonly HashSet<int> Hidden = new HashSet<int>();   // SNES slots hidden (F5 hide/kill, or hidden from the start)
            public bool HoldEnd;   // F5 mode 4: the battle can't end until a monster is shown
            public double HoldUntil;   // a hold's safety limit (Clock): nothing keeps a battle open for good
            // a death's entries/exits, applied once the dying monster has left the field (the PR is still
            // running its death when FinalAttackAct asks): (the dying unit, its slot, its SNES id, the
            // entries, the battle time after which they apply anyway)
            public readonly List<(BattleEnemyData unit, int slot, int snesId, List<SnesAiAction> entries, double by)> Deferred =
                new List<(BattleEnemyData, int, int, List<SnesAiAction>, double)>();
            // members coming in, one at a time (InBattleEnemy keeps one pending load: a second call drops the first)
            public readonly Queue<(int prId, int cell)> Creating = new Queue<(int, int)>();
            public int InFlightCell;
            public double InFlightSince;
            // what last hit each monster slot (its counter script reads it)
            public readonly int[] Retal = Enumerable.Repeat(-1, 6).ToArray();
            public readonly int[] RetalCmdUnit = Enumerable.Repeat(-1, 6).ToArray();
            public readonly int[] RetalCmd = new int[6], RetalAtk = new int[6], RetalItm = new int[6];
            public readonly int[] RetalElemUnit = Enumerable.Repeat(-1, 6).ToArray();
            public readonly int[] RetalElem = new int[6];
            public readonly int[] RetalFlags = new int[6];
            public readonly int[] PrevAttacker = Enumerable.Repeat(-1, 6).ToArray();
            // BNW's bosses at 0 HP (kept at 1 by UpdateConditionHp_Prefix; their scripts see 0): UniqueId ->
            // the battle time they got there
            public readonly Dictionary<int, double> AtZero = new Dictionary<int, double>();
            public readonly HashSet<int> Dying = new HashSet<int>();   // their script let them die
            public readonly HashSet<int> PendingKill = new HashSet<int>();   // FB 0B 07 this pass: dies next frame
            public readonly HashSet<int> EraseOnDeath = new HashSet<int>();   // its death's final attack: the erase-all
            // "everyone dies like a boss": if the fight still has monsters standing at this Clock (the PR
            // raised no final attack for the erase-all), each dies the game's own way (0: none due)
            public double EraseAllAt;
            public readonly HashSet<int> Erased = new HashSet<int>();   // killed by that: no death script of their own
            public readonly HashSet<int> ZeroCountered = new HashSet<int>(), ZeroForced = new HashSet<int>();   // at 0 HP: its death script ran / its counter pass was asked for
            public System.Random Rng = new System.Random();
        }

        internal static Fight Current;
        static SnesAiData D => Mod?.Data;
        static string Tag => Mod?.Tag ?? "kit";
        static bool Verbose => Mod != null && Mod.Verbose;
        static void Log(string line) => Kit.Info($"[{Tag}] enemy AI{line}");

        // the battle's units by SNES unit index (0-3 party slots, 4-9 monster slots); null where empty
        internal static BattleUnitData[] Units()
        {
            var u = new BattleUnitData[10];
            var bm = BattlePlugManager.Instance();
            if (bm == null) return u;
            try
            {
                foreach (var p in bm.GetPlayerUnits())
                {
                    var pd = p?.TryCast<BattlePlayerData>();
                    if (pd != null && pd.corpsIndex >= 0 && pd.corpsIndex < 4) u[pd.corpsIndex] = pd;
                }
                var f = Current;
                foreach (var e in bm.GetEnemyUnits())
                {
                    var ed = e?.TryCast<BattleEnemyData>();
                    if (ed == null || f == null) continue;
                    if (!f.Slot.ContainsKey(ed.UniqueId)) Join(f, ed);   // brought in since (F5)
                    if (!f.Slot.TryGetValue(ed.UniqueId, out int s)) continue;
                    u[4 + s] = ed;
                }
            }
            catch (Exception ex) { Hooks.WarnOnce("enemy-ai-units", ex); }
            return u;
        }

        static bool HasCondition(BattleUnitData u, int type)
        {
            var list = u?.BattleUnitDataInfo?.Parameter?.CurrentConditionList;
            if (list == null) return false;
            foreach (var c in list)
                if (c != null && (int)c.ConditionType == type) return true;
            return false;
        }

        static bool IsDead(BattleUnitData u)
        {
            var p = u?.BattleUnitDataInfo?.Parameter;
            if (p == null) return true;
            return p.CurrentHP <= 0 || HasCondition(u, 5) || HasCondition(u, 11);   // Wound / Petrify
        }

        // the battle as FF6's AI sees it
        internal sealed class View : ISnesAiBattle
        {
            readonly Fight f;
            readonly BattleUnitData[] u;
            public View(Fight fight, BattleUnitData[] units) { f = fight; u = units; }

            public int Rand(int n) => n <= 1 ? 0 : f.Rng.Next(n);

            public int CharActor(int slot)
            {
                var pd = u[slot]?.TryCast<BattlePlayerData>();
                int id = pd?.ownedCharacterData?.characterStatusId ?? 0;
                return id >= 1 && id <= 14 ? id - 1 : (id > 0 ? id : -1);   // PR character 1-14 = SNES actor 0-13
            }

            bool Out(int unit) => unit >= 4 && f.Hidden.Contains(unit - 4);

            public bool MonsterPresent(int slot) => u[4 + slot] != null && !IsDead(f, u[4 + slot]) && !HasCondition(u[4 + slot], 416) && !Out(4 + slot);

            public int TargetableMask()
            {
                int m = 0;
                for (int i = 0; i < 10; i++)
                    if (u[i] != null && !IsDead(f, u[i]) && !HasCondition(u[i], 416) && !HasCondition(u[i], 420) && !Out(i)) m |= SnesAi.MaskOf(i);
                return m;
            }

            public bool Dead(int unit) => u[unit] != null && IsDead(f, u[unit]);

            public int Status(int unit, int word)
            {
                var x = u[unit];
                if (x == null) return 0;
                int w = 0;
                for (int b = 0; b < 16; b++)
                {
                    int bit = word * 16 + b, type = StatusCondition[bit];
                    bool on = type == -2 ? NearFatal(x) : type > 0 && HasCondition(x, type);
                    if (on) w |= 1 << b;
                }
                return w;
            }

            static bool NearFatal(BattleUnitData x)
            {
                var p = x?.BattleUnitDataInfo?.Parameter;
                return p != null && p.CurrentHP > 0 && p.CurrentHP * 8 <= p.ConfirmedMaxHp();
            }

            // a BNW boss held at 0 HP is at 0 for its script (the game keeps it at 1)
            public int Hp(int unit)
            {
                var ed = unit >= 4 ? u[unit]?.TryCast<BattleEnemyData>() : null;
                if (ed != null && f.AtZero.ContainsKey(ed.UniqueId)) return 0;
                return u[unit]?.BattleUnitDataInfo?.Parameter?.CurrentHP ?? 0;
            }
            public int Mp(int unit) => u[unit]?.BattleUnitDataInfo?.Parameter?.CurrentMP ?? 0;
            public int Level(int unit) => u[unit]?.BattleUnitDataInfo?.Parameter?.ConfirmedLevel() ?? 0;
            public int WeakElements(int unit)    // monsters: their data's weak elements (characters: none)
            {
                if (unit < 4 || u[unit] == null) return 0;
                var ed = u[unit].TryCast<BattleEnemyData>();
                return ed != null && f.SnesId.TryGetValue(ed.UniqueId, out int id) && D?.Weak.TryGetValue(id, out int w) == true ? w : 0;
            }
            public bool CharPresentFlag(int slot) => u[slot] != null;
            public int BattleTimer() => (int)(f.BattleTime / TickSeconds);
            public int MonsterTimer(int unit) => unit >= 4 ? (int)(f.MonsterTime[unit - 4] / TickSeconds) : 0;
            public int Formation() => f.Formation;
            public int CharsAlive() => Enumerable.Range(0, 4).Count(s => u[s] != null && !IsDead(u[s]));
            public int MonstersAlive() => Enumerable.Range(4, 6).Count(s => u[s] != null && !IsDead(f, u[s]) && !Out(s));
            public int MonsterTypesAlive()
            {
                var kinds = new HashSet<int>();
                for (int s = 4; s < 10; s++)
                    if (u[s] != null && !IsDead(u[s]) && !Out(s)) kinds.Add(u[s].GetMonster()?.Id ?? 0);
                return kinds.Count;
            }
            public int MonstersInBattleMask()
            {
                int m = 0;
                for (int s = 0; s < 6; s++) if (u[4 + s] != null || (f.Members != null && s < f.Members.Length && f.Members[s] != null)) m |= 1 << s;
                return m;
            }
            public int MonstersAliveMask()
            {
                int m = 0;
                for (int s = 0; s < 6; s++) if (u[4 + s] != null && !IsDead(f, u[4 + s]) && !Out(4 + s)) m |= 1 << s;
                return m;
            }
            public bool GauRecruited()
            {
                try
                {
                    foreach (var c in Last.Management.UserDataManager.Instance().OwnedCharacterList)
                        if (c?.characterStatusId == 12) return true;
                }
                catch (Exception) { }
                return false;
            }
            public byte BattleVar(int i) => f.Vars[i];
            public int RetalTarget(int unit) => unit >= 4 ? f.Retal[unit - 4] : -1;
            public int RetalCmdTarget(int unit) => unit >= 4 ? f.RetalCmdUnit[unit - 4] : -1;
            public int RetalCommand(int unit) => unit >= 4 ? f.RetalCmd[unit - 4] : 0;
            public int RetalAttack(int unit) => unit >= 4 ? f.RetalAtk[unit - 4] : 0;
            public int RetalItem(int unit) => unit >= 4 ? f.RetalItm[unit - 4] : 0;
            public int RetalElemTarget(int unit) => unit >= 4 ? f.RetalElemUnit[unit - 4] : -1;
            public int RetalElements(int unit) => unit >= 4 ? f.RetalElem[unit - 4] : 0;
            public int RetalFlags(int unit) => unit >= 4 ? f.RetalFlags[unit - 4] : 0;
            public int PrevAttacker(int unit) => unit >= 4 ? f.PrevAttacker[unit - 4] : -1;
            public int TargettingTarget(int unit) => -1;
            public void ClearTargettingTarget(int unit) { }
        }

        // FF6's timers run on battle time (the PR stops time while an action plays; the SNES doesn't: close
        // enough for the scripts' "after N seconds" checks)
        public static void Update()
        {
            if (Mod == null || !Mod.Active) return;
            var bm = BattlePlugManager.Instance();
            if (bm == null || !bm.IsBattle()) return;
            StartHides();
            if (Current == null || fightSerial != battleSerial) return;   // the last battle's fight: nothing of it carries over
            Tick(Time.deltaTime);
            RunDeferred(Current);
            PumpCreations(Current);
            ZeroHpChecks(Current);
        }

        internal static void Tick(float dt)
        {
            var f = Current;
            if (f == null) return;
            f.BattleTime += dt;
            f.Clock += dt;
            var u = Units();
            for (int s = 0; s < 6; s++)
                if (u[4 + s] != null && !IsDead(u[4 + s]) && !f.Hidden.Contains(s)) f.MonsterTime[s] += dt;
        }

        // ---- what the interpreter's actions become in the PR --------------------------------------------
        const int Nothing = 696;   // the PR AIs' idle ability

        // SNES attack index -> PR ability: its Battle (EE) and special (EF), else the spell/skill map; its
        // Battle when there's nothing to cast
        public static int BaseAbility(SnesAiData d, int snesMonster, int attack)
        {
            if (d == null) return 0;
            int battle = d.Battle.TryGetValue(snesMonster, out int bb) ? bb : 1;
            if (attack == 0xEE) return battle;
            if (attack == 0xEF) return d.Special.TryGetValue(snesMonster, out int sp) ? sp : battle;
            int a = attack < d.Attacks.Length ? d.Attacks[attack] : 0;
            return a > 0 ? a : battle;
        }

        // an FF6 target mask -> the PR units (null: the ability's default targets)
        internal static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BattleUnitData> TargetsOf(int mask, BattleUnitData[] units)
        {
            if (mask == 0) return null;
            var list = new List<BattleUnitData>();
            for (int i = 0; i < 10; i++)
                if ((mask & SnesAi.MaskOf(i)) != 0 && units[i] != null && !IsDead(units[i])) list.Add(units[i]);
            return list.Count == 0 ? null : new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BattleUnitData>(list.ToArray());
        }

        // variable / switch changes: $00-$23 the battle's, from $24 the monster's own byte
        static void ApplyVars(Fight f, SnesAiMonster m, SnesAiAction a)
        {
            if (a.Op == SnesAiOp.ChangeVar)
            {
                if (SnesAi.IsBattleVar(a.B1)) f.Vars[a.B1] = SnesAi.ChangeVar(f.Vars[a.B1], a.B2);
                else m.Var = SnesAi.ChangeVar(m.Var, a.B2);
            }
            else if (a.Op == SnesAiOp.ChangeSwitch)
            {
                if (SnesAi.IsBattleVar(a.B2)) f.Vars[a.B2] = SnesAi.ChangeSwitch(f.Vars[a.B2], a.B1, a.B3);
                else m.Var = SnesAi.ChangeSwitch(m.Var, a.B1, a.B3);
            }
        }

        // FB misc effects the battle can do here (C2/1E5E MiscAIEffectTbl): timers; with the mod's
        // ScriptStatusEffects a status set/cleared on itself (0B/0C: status index 0-31, 0B 07 = Wound: it
        // dies) and the battle's end (02: as "everyone dies like a boss"); the rest is logged
        static void ApplyMisc(Fight f, int slot, SnesAiAction a, BattleUnitData me, List<SnesAiAction> entries)
        {
            bool status = Mod != null && Mod.ScriptStatusEffects;
            var ed = me?.TryCast<BattleEnemyData>();
            switch (a.B1)
            {
                case 0x00: f.MonsterTime[slot] = 0; return;   // reset this monster's timer
                case 0x04: f.BattleTime = 0; return;          // reset the battle timer
                case 0x02 when status:
                    entries.Add(new SnesAiAction { Op = SnesAiOp.EntryExit, B1 = 0x0C, B2 = 5, B3 = 0xFF });
                    return;
                case 0x0B when status && ed != null && a.B2 == 7:
                    f.PendingKill.Add(ed.UniqueId);            // after this pass (Update): the game's death
                    return;
                case 0x0B when status && ed != null && a.B2 < 32 && StatusCondition[a.B2] > 0:
                    BattlePlugManager.Instance()?.EventCommand?.AddConditionEnemy(ed.UniqueId, StatusCondition[a.B2]);
                    return;
                case 0x0C when status && ed != null && a.B2 < 32 && StatusCondition[a.B2] > 0:
                    BattlePlugManager.Instance()?.EventCommand?.RecoveryConditionEnemy(ed.UniqueId, StatusCondition[a.B2]);
                    return;
            }
            if (Seen.Add($"misc {a.B1:x2} {a.B2:x2}")) Log($": misc effect {a.B1:x2} {a.B2:x2} not done yet");
        }

        internal static readonly HashSet<string> Seen = new HashSet<string>();
    }
}
