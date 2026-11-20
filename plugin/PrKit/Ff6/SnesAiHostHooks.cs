using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Last.Battle;
using Last.Interpreter;

namespace PrKit.Ff6
{
    // SnesAiHost, part 2: the hooks. A monster's PR script is a shim (tools/port_ai.py shim_script) whose
    // rows carry markers: LoopAct [MarkTurn] (its turn), CounterAct [MarkCounter] (after a hit),
    // FinalAttackAct [MarkFinal] (its death). When the PR's interpreter is about to run such a row for real
    // (its flag set, the turn's row: the checks BattleAI.LoopAct/CounterAct/FinalAttackAct make), the host
    // runs the monster's SNES script and rewrites the row's operands to what it does, then lets the PR's
    // own instruction queue it (its bookkeeping, act types, waits and battle lines stay the game's): the
    // marker stays first with weight 0 (LoopAct queues it 0 times; CounterAct/FinalAttackAct never pick a
    // weight-0 entry), the abilities follow with their counts (LoopAct can't hold one id twice), the
    // lines go in sValues (LoopAct files them for every ability it queues; ExecMsg shows them). An idle
    // turn is the PR's own "nothing" (696). Targets: CreateActData, first, takes each queued act's
    // targets from the plan. Variable/switch changes and timers apply at once.
    public static partial class SnesAiHost
    {
        public const int MarkTurn = 7680, MarkCounter = 7681, MarkFinal = 7682, EraseAll = 735;
        static int battleSerial, fightSerial = -1;
        public static int Turns, Counters, Finals;     // for self-tests and the log
        public static string LastTurn = "", LastCounter = "", LastFinal = "";

        // PR "received command" (the AI's ForAiType) -> SNES battle command (ai_compile SNES_CMD_TO_PR)
        static readonly Dictionary<int, int> PrCmdToSnes = new Dictionary<int, int>
            { { 4, 0x00 }, { 2, 0x02 }, { 607, 0x05 }, { 608, 0x06 }, { 601, 0x07 }, { 611, 0x09 }, { 602, 0x0A }, { 504, 0x0C }, { 9, 0x19 } };
        // PR attribute -> SNES element bit (ai_compile ELEM_ATTR: bit i = attribute ElemAttr[i])
        static readonly int[] ElemAttr = { 1, 2, 3, 6, 8, 7, 4, 5 };

        static System.Reflection.MethodInfo getAse, updateConditionHp;

        public static void Patch(Harmony h, SnesAiMod mod)
        {
            Mod = mod;
            var t = typeof(Last.Interpreter.Instructions.BattleAI);
            foreach (var (name, pre) in new[] { ("LoopAct", nameof(LoopAct_Prefix)), ("CounterAct", nameof(CounterAct_Prefix)), ("FinalAttackAct", nameof(FinalAttackAct_Prefix)) })
            {
                var m = AccessTools.Method(t, name);
                if (m == null) throw new MissingMethodException("BattleAI." + name);
                h.Patch(m, prefix: new HarmonyMethod(typeof(SnesAiHost), pre));
            }
            getAse = AccessTools.Method(t, "GetActSelectEnemy") ?? throw new MissingMethodException("BattleAI.GetActSelectEnemy");
            // test runs: a note the first time any monster's AI pass runs (every pass starts with SyncAI)
            var sync = AccessTools.Method(t, "SyncAI");
            if (sync != null) h.Patch(sync, prefix: new HarmonyMethod(typeof(SnesAiHost), nameof(SyncAI_Prefix)));
            var cad = AccessTools.Method(typeof(ActSelectEnemy), "CreateActData") ?? throw new MissingMethodException("ActSelectEnemy.CreateActData");
            h.Patch(cad, prefix: new HarmonyMethod(typeof(SnesAiHost), nameof(CreateActData_Prefix)) { priority = Priority.First });
            // self-tests: what the AI's ViewMes hands the message window (BattleAI.ViewMes puts the act's next
            // key into the running instruction's sValues, then runs the battle's Msg)
            var vm = AccessTools.Method(t, "ViewMes");
            if (vm != null) h.Patch(vm, postfix: new HarmonyMethod(typeof(SnesAiHost), nameof(ViewMes_Postfix)));
            var create = AccessTools.Method(typeof(InstantiateManager), "Create");
            if (create != null) h.Patch(create, postfix: new HarmonyMethod(typeof(SnesAiHost), nameof(Create_Postfix)));
            // what hits a monster (BNW's counter rules: was it a counterattack, did it take MP): the act as it starts
            var provider = Hooks.TypeByName("Serial.FF6.Calc.FunctionProvider");
            var act = provider == null ? null : AccessTools.Method(provider, "CreateActFunction");
            if (act != null) h.Patch(act, prefix: new HarmonyMethod(typeof(SnesAiHost), nameof(Act_Prefix)));
            // BNW's bosses: HP loss doesn't kill them, their script does (BattleConditionController.UpdateConditionHp
            // is where the PR turns 0 HP into death; it keeps monsters with its own "can't die" flag at 1 HP)
            updateConditionHp = AccessTools.Method(typeof(BattleConditionController), "UpdateConditionHp");
            if (updateConditionHp != null)
                h.Patch(updateConditionHp, prefix: new HarmonyMethod(typeof(SnesAiHost), nameof(UpdateConditionHp_Prefix)));
            Log(": monsters' turns, counters and deaths run their SNES scripts" + (mod.Data != null ? $" ({mod.Data.Rules.Name} rules)" : ""));
        }

        public static int AiPasses, ActsSeen;   // self-tests: every AI pass (SyncAI) and act (CreateActFunction) seen

        public static void SyncAI_Prefix(BattleAICore __0)
        {
            AiPasses++;
            if (Verbose) Hooks.NoteOnce("ai-sync", () => $"[{Tag}] enemy AI: first AI pass seen (SyncAI; row marker {Marker(__0)})");
        }

        static int battleParty;   // the party InstantiateManager.Create made the battle from

        public static void Create_Postfix(int __0)
        {
            battleSerial++;
            battleParty = __0;
        }

        public static void ViewMes_Postfix(BattleAICore __0, int __result)
        {
            try
            {
                var sv = __0?.op?.sValues;
                string k = sv != null && sv.Length > 0 ? sv[0] : "";
                // ViewMes runs again while the window shows the line: count it once per turn; and the
                // instruction keeps the last key it showed, so only when the staged act's lines hold it
                if (string.IsNullOrEmpty(k) || !FiledKeys.Contains(k) || (k == LastLine && shownAtTurn == Turns)) return;
                var dic = __0.msgExecuteInfos;
                int st = Staging();
                if (dic == null || !dic.ContainsKey(st) || dic[st] == null || !dic[st].Contains(k)) return;
                LastLine = k;
                shownAtTurn = Turns;
                LinesShown++;
                if (LinesSeen.Count < 200) LinesSeen.Add(k);
                if (Verbose) Log($": battle line {k} shown");
            }
            catch (Exception e) { Hooks.WarnOnce("enemy-ai-viewmes", e); }
        }

        static int Staging()
        {
            try { return BattlePlugManager.Instance()?.BattleActExection?.GetStagingAbilityId() ?? -1; } catch { return -2; }
        }

        // battle lines: the keys this host filed, and which of them the battle's message pass showed
        static readonly HashSet<string> FiledKeys = new HashSet<string>();
        public static int LinesQueued, LinesShown;
        public static readonly List<string> LinesSeen = new List<string>();
        static string LastLine = "";
        static int shownAtTurn = -1;

        static void Filed(List<string> lines)
        {
            if (lines.Count == 0) return;
            FiledKeys.UnionWith(lines);
            LinesQueued += lines.Count;
        }

        static bool Active => Mod != null && Mod.Active;

        static int Marker(BattleAICore bc)
        {
            var iv = bc?.op?.iValues;
            return iv != null && iv.Length > 0 ? iv[0] : 0;
        }

        // the planned targets of the acts a row is about to queue, by the unit that acts (in order)
        static readonly Dictionary<IntPtr, Queue<(int ability, Il2CppReferenceArray<BattleUnitData> targets)>> planned =
            new Dictionary<IntPtr, Queue<(int, Il2CppReferenceArray<BattleUnitData>)>>();

        // the row's operands: the marker (weight 0), then the plan's abilities with their counts, the lines
        static void Rewrite(BattleAICore bc, int marker, List<(int ability, Il2CppReferenceArray<BattleUnitData> targets)> q, List<string> lines, bool counts)
        {
            var ids = new List<int> { marker };
            var weights = new List<double> { 0 };
            foreach (var (ab, _) in q)
            {
                int at = ids.IndexOf(ab, 1);
                if (at > 0 && counts) weights[at] += 1;
                else if (at < 0) { ids.Add(ab); weights.Add(counts ? 1 : 100); }
            }
            var op = bc.op;
            op.iValues = new Il2CppStructArray<int>(ids.ToArray());
            op.rValues = new Il2CppStructArray<double>(weights.ToArray());
            op.sValues = new Il2CppStringArray(lines.ToArray());
            Filed(lines);
        }

        static void Plan(BattleUnitData me, List<(int ability, Il2CppReferenceArray<BattleUnitData> targets)> q)
        {
            if (me == null) return;
            var queue = new Queue<(int, Il2CppReferenceArray<BattleUnitData>)>();
            foreach (var x in q) if (x.targets != null) queue.Enqueue(x);
            planned[me.Pointer] = queue;
        }

        // first of the CreateActData prefixes: the act's targets from the plan (a mod's own prefixes, such as
        // Worlds Collide's ability scaling, run after this)
        public static void CreateActData_Prefix(ActSelectEnemy __instance, ref int __0, ref Il2CppReferenceArray<BattleUnitData> __1)
        {
            try
            {
                var me = __instance?.BattleUnitData;
                if (me == null || !planned.TryGetValue(me.Pointer, out var queue) || queue.Count == 0) return;
                if (queue.Peek().ability != __0) return;
                __1 = queue.Dequeue().targets;
            }
            catch (Exception e) { Hooks.WarnOnce("enemy-ai-targets", e); }
        }

        static ActSelectEnemyController Controller(BattleAICore bc) =>
            (getAse?.Invoke(null, new object[] { bc }) as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<ActSelectEnemyController>();

        // the battle's state: made fresh for each battle (InstantiateManager.Create), monsters by UniqueId
        // in slot order, each with its script
        static Fight EnsureFight()
        {
            if (Current != null && fightSerial == battleSerial) return Current;
            var f = new Fight();
            fightSerial = battleSerial;
            Current = f;
            var d = D;
            var bm = BattlePlugManager.Instance();
            try
            {
                int party = battleParty > 0 ? battleParty : bm?.MonsterParty?.Id ?? 0;
                int src = Mod?.PartySource(party) ?? party;
                f.Formation = d != null && d.Formations.TryGetValue(src, out int form) ? form : -1;
                // the cells' SNES slots: the battle's own party (its layout), else its look source's
                f.CellSlots = d != null && (d.PartySlots.TryGetValue(party, out var cs) || d.PartySlots.TryGetValue(src, out cs)) ? cs : null;
                f.Members = d != null && (d.PartyMembers.TryGetValue(party, out var pm) || d.PartyMembers.TryGetValue(src, out pm)) ? pm : null;
                if (f.Members != null)
                    for (int s = 0; s < f.Members.Length; s++)
                        if (f.Members[s] != null && f.Members[s][2] == 0) f.Hidden.Add(s);   // hidden from the start (shown by its script)
                if (d != null && f.Formation < 0 && d.Formations.TryGetValue(party, out int form2)) f.Formation = form2;
                var enemies = new List<BattleEnemyData>();
                foreach (var e in bm.GetEnemyUnits())
                {
                    var ed = e?.TryCast<BattleEnemyData>();
                    if (ed != null) enemies.Add(ed);
                }
                foreach (var ed in enemies.OrderBy(x => x.UniqueId)) Join(f, ed);
                Log($": party {party} (formation {f.Formation}): " +
                    string.Join(", ", f.Slot.Select(kv => $"slot {kv.Value} = monster {f.SnesId[kv.Key]} (unique {kv.Key})")) +
                    (Verbose ? "; PR scripts " + string.Join(", ", enemies.Select(e => $"{e.GetMonster()?.Id}:{e.GetMonster()?.ScriptId}")) : ""));
            }
            catch (Exception e) { Hooks.WarnOnce("enemy-ai-fight", e); }
            return f;
        }

        static void Join(Fight f, BattleEnemyData ed)
        {
            if (f.Slot.ContainsKey(ed.UniqueId)) return;
            // the SNES slot of its cell (uniqueId = the party's cell 1-9), else the first free one
            int slot = f.CellSlots != null && ed.UniqueId > 0 && ed.UniqueId < f.CellSlots.Length ? f.CellSlots[ed.UniqueId] : -1;
            if (slot < 0 || f.Slot.ContainsValue(slot))
            {
                slot = -1;
                for (int s = 0; s < 6; s++) if (!f.Slot.ContainsValue(s)) { slot = s; break; }
            }
            if (slot < 0) return;
            var d = D;
            int prId = ed.GetMonster()?.Id ?? 0;
            int id = d != null && d.Copies.TryGetValue(prId, out int orig) ? orig : prId - 1;
            f.Slot[ed.UniqueId] = slot;
            f.SnesId[ed.UniqueId] = id;
            if (d != null && d.Scripts.TryGetValue(id, out var script)) f.Monsters[ed.UniqueId] = SnesAiMonster.FromScript(script, d.Rules);
        }

        // the monster this core runs for (its unit's act-select controller is the core's)
        static BattleEnemyData Owner(ActSelectEnemyController c)
        {
            if (c == null) return null;
            var all = BattlePlugManager.Instance()?.GetEnemyUnits();
            if (all == null) return null;
            foreach (var e in all)
            {
                var ctrl = e?.BattleUnitDataInfo?.ActSelectController;
                if (ctrl != null && ctrl.Pointer == c.Pointer) return e;
            }
            return null;
        }

        static int UnitIndex(BattleUnitData x, BattleUnitData[] units)
        {
            if (x == null) return -1;
            for (int i = 0; i < 10; i++) if (units[i] != null && units[i].Pointer == x.Pointer) return i;
            return -1;
        }

        // battle lines: the PR's own lines (its keys: the game's text in the player's language), else the
        // data's text under keys of the mod's own; shown with the act
        static string[] LineKeys(int message)
        {
            var d = D;
            if (d == null) return Array.Empty<string>();
            if (d.MessageKeys.TryGetValue(message, out var pr) && pr.Length > 0) return pr;
            if (!d.Messages.TryGetValue(message, out var lines) || lines.Length == 0) return Array.Empty<string>();
            var keys = new string[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                keys[i] = $"{Tag.ToUpperInvariant()}_BTL_{message:X2}_{i}";
                Mod.SetText(keys[i], lines[i]);
            }
            return keys;
        }

        // the interpreter's actions -> (ability, targets) acts and battle lines; everything else applied now
        static List<(int ability, Il2CppReferenceArray<BattleUnitData> targets)> Plan(Fight f, SnesAiMonster m, int snesId, int slot,
            List<SnesAiAction> acts, BattleUnitData[] units, List<string> lines, List<SnesAiAction> entries)
        {
            var q = new List<(int, Il2CppReferenceArray<BattleUnitData>)>();
            foreach (var a in acts)
            {
                switch (a.Op)
                {
                    case SnesAiOp.Attack:
                        {
                            int ab = Mod.AbilityFor(snesId, a.Attack);
                            if (ab > 0) q.Add((ab, TargetsOf(a.Targets, units)));
                            break;
                        }
                    case SnesAiOp.Item:      // F6 00: uses an item (the item's own effect, as Square ported Harvester's Hi-Potion)
                    case SnesAiOp.Throw:     // F6 01: throws a weapon (a hit with its power: Square's 740-756)
                    case SnesAiOp.Command:   // F4: Steal 693, Jump 1391, GP Rain 694 (Capture: Steal)
                        {
                            var d = D;
                            var map = a.Op == SnesAiOp.Item ? d?.ItemUse : a.Op == SnesAiOp.Throw ? d?.Throws : d?.Commands;
                            int key = a.Op == SnesAiOp.Command ? a.Command : a.Attack;
                            if (map != null && map.TryGetValue(key, out int ab) && ab > 0) q.Add((ab, TargetsOf(a.Targets, units)));
                            else if (Seen.Add($"{a.Op}:{key}:{snesId}")) Log($": monster {snesId}: {a} has no PR ability");
                            break;
                        }
                    case SnesAiOp.ChangeVar:
                    case SnesAiOp.ChangeSwitch:
                        ApplyVars(f, m, a);
                        break;
                    case SnesAiOp.Misc:
                        ApplyMisc(f, slot, a, units[4 + slot], entries);
                        break;
                    case SnesAiOp.Message:
                        lines.AddRange(LineKeys(a.B1));
                        break;
                    case SnesAiOp.EntryExit:
                        entries.Add(a);
                        break;
                    default:
                        if (Seen.Add($"{a.Op}:{snesId}")) Log($": monster {snesId}: {a} not done yet");
                        break;
                }
            }
            return q;
        }

        static string Show(List<(int ability, Il2CppReferenceArray<BattleUnitData> targets)> q) =>
            string.Join(", ", q.Select(x => $"{x.ability}" + (x.targets == null ? "" : $"->{x.targets.Length}")));

        // ---- a turn ---------------------------------------------------------------------------------------
        public static bool LoopAct_Prefix(BattleAICore __0, ref int __result)
        {
            if (Verbose) Hooks.NoteOnce("ai-LoopAct_Prefix", () => $"[{Tag}] enemy AI: first LoopAct row seen (marker {Marker(__0)}, host on {Active})");
            if (!Active || Marker(__0) != MarkTurn) return true;
            try
            {
                var c = Controller(__0);
                // only the pass where the row really acts (BattleAI.LoopAct: the normal flag, its turn)
                if (c != null && c.IsReadyActNormal() && c.GetActTurn() == __0.turnCounter && Turn(__0, c))
                {
                    __0.turnCounter = __0.turnCounter + 1;   // the next row, as Act/LoopAct move on
                    __result = 0;
                    return false;
                }
            }
            catch (Exception e) { Hooks.WarnOnce("enemy-ai-turn", e); }
            return true;   // the PR's LoopAct queues what the row now holds
        }

        // the lines an act shows, filed under its ability as the PR's Act does with a row's keys (always, so
        // a line filed for an earlier act of the same ability never shows again: ViewMes reads the act's)
        static void FileLines(BattleAICore bc, int ability, List<string> lines)
        {
            var dic = bc.msgExecuteInfos;
            if (dic == null) return;
            var arr = new Il2CppStringArray(lines.ToArray());
            if (dic.ContainsKey(ability)) dic[ability] = arr; else dic.Add(ability, arr);
            Filed(lines);
        }

        // true: the turn's one act is queued here the way the PR's Act queues it (a normal act, its lines
        // shown: the battle's message pass only shows lines for those); false: several acts, the row now
        // holds them for the PR's LoopAct
        static bool Turn(BattleAICore bc, ActSelectEnemyController c)
        {
            var f = EnsureFight();
            var me = Owner(c);
            var q = new List<(int, Il2CppReferenceArray<BattleUnitData>)>();
            var lines = new List<string>();
            var acts = new List<SnesAiAction>();
            int snesId = -1, slot = 0;
            if (me != null)
            {
                Join(f, me);
                var units = Units();
                int uid = me.UniqueId;
                slot = f.Slot.TryGetValue(uid, out int s) ? s : 0;
                snesId = f.SnesId.TryGetValue(uid, out int sid) ? sid : -1;
                if (f.Monsters.TryGetValue(uid, out var m) && !f.Hidden.Contains(slot))
                {
                    acts = SnesAi.RunMain(m, 4 + slot, new View(f, units));
                    var entries = new List<SnesAiAction>();
                    q = Plan(f, m, snesId, slot, acts, units, lines, entries);
                    if (EraseAllIn(entries)) q.Insert(0, (EraseAll, null));
                    else
                    {
                        ApplyEntries(f, slot, snesId, entries);
                        if (entries.Count > 0 && AllGone(f)) q.Insert(0, (EraseAll, null));
                    }
                }
            }
            if (q.Count == 0) q.Add((Nothing, null));   // an idle turn: the PR's own "nothing"
            Turns++;
            LastTurn = $"monster {snesId} slot {slot}: {string.Join("; ", acts)} => {Show(q)}";
            if (Verbose) Log(" turn: " + LastTurn);
            if (q.Count == 1)
            {
                c.SetActAbilityNormal(q[0].Item1, q[0].Item2, null);
                FileLines(bc, q[0].Item1, lines);
                return true;
            }
            Rewrite(bc, MarkTurn, q, lines, true);
            Plan(me, q);
            return false;
        }

        // ---- what hit it ----------------------------------------------------------------------------------
        // the act that's starting: whether it's a counterattack (BNW: those never set off counters) and the
        // monsters' MP before it (MP damage: BNW's FC 05 xx 02)
        static IntPtr actUser;
        static bool actCounter;
        static int actsNoted;
        static readonly Dictionary<int, int> mpBefore = new Dictionary<int, int>();

        public static void Act_Prefix(BattleActData battleActData)
        {
            if (!Active || battleActData == null) return;
            ActsSeen++;
            try
            {
                actUser = battleActData.AttackUnitData?.Pointer ?? IntPtr.Zero;
                actCounter = battleActData.isCounter;
                // test runs: the first monster acts as the game makes them (a shim's marker here = its row wasn't answered)
                var by = battleActData.AttackUnitData?.TryCast<BattleEnemyData>();
                if (Verbose && by != null && actsNoted < 40)
                {
                    actsNoted++;
                    var ids = new List<int>();
                    if (battleActData.abilityList != null) foreach (var a in battleActData.abilityList) if (a != null) ids.Add(a.Id);
                    Log($": act by monster {by.GetMonster()?.Id} (unique {by.UniqueId}): abilities [{string.Join(" ", ids)}]{(battleActData.isCounter ? ", a counter" : "")}");
                }
                mpBefore.Clear();
                var bm = BattlePlugManager.Instance();
                if (bm == null) return;
                foreach (var e in bm.GetEnemyUnits())
                {
                    var p = e?.BattleUnitDataInfo?.Parameter;
                    if (p != null) mpBefore[e.UniqueId] = p.CurrentMP;
                }
            }
            catch (Exception e) { Hooks.WarnOnce("enemy-ai-act", e); }
        }

        // BNW's $327D for a hit: $80 a counterattack, $02 MP damage, $01 melee (physical and row-respecting:
        // Fight / Capture, a monster's Battle; ranged weapons aren't told apart)
        static int HitFlags(BattleEnemyData me, BattleUnitData attacker, int cmd, int attack)
        {
            int flags = 0;
            if (actCounter && attacker != null && attacker.Pointer == actUser) flags |= 0x80;
            var p = me?.BattleUnitDataInfo?.Parameter;
            if (p != null && mpBefore.TryGetValue(me.UniqueId, out int mp) && p.CurrentMP < mp) flags |= 0x02;
            if (cmd == 0x00 || cmd == 0x06 || attack == 0xEE) flags |= 0x01;
            return flags;
        }

        // ---- a counter --------------------------------------------------------------------------------------
        public static bool CounterAct_Prefix(BattleAICore __0, ref int __result)
        {
            if (Verbose) Hooks.NoteOnce("ai-CounterAct_Prefix", () => $"[{Tag}] enemy AI: first CounterAct row seen (marker {Marker(__0)}, host on {Active})");
            if (!Active || Marker(__0) != MarkCounter) return true;
            try
            {
                var c = Controller(__0);
                if (c == null || !c.IsReadyActCounter()) return true;
                if (Counter(__0, c)) return true;   // the PR's CounterAct queues it (at the attacker unless self-targeted)
                __result = 0;
                return false;                        // no counterattack (its variables changed all the same)
            }
            catch (Exception e) { Hooks.WarnOnce("enemy-ai-counter", e); return true; }
        }

        static bool Unresponsive(BattleUnitData me) =>
            HasCondition(me, 7) || HasCondition(me, 12) || HasCondition(me, 410) || HasCondition(me, 603) || HasCondition(me, 17) || HasCondition(me, 630);

        static bool Counter(BattleAICore bc, ActSelectEnemyController c)
        {
            var f = EnsureFight();
            var me = Owner(c);
            if (me == null || IsDead(me)) return false;
            Join(f, me);
            var units = Units();
            int uid = me.UniqueId, slot = f.Slot.TryGetValue(uid, out int s) ? s : 0, snesId = f.SnesId.TryGetValue(uid, out int sid) ? sid : -1;
            if (!f.Monsters.TryGetValue(uid, out var m)) return false;
            if (f.Hidden.Contains(slot)) return false;   // out of the battle (an attack already on its way hit it)
            // what hit it (the retaliation data its counter script reads)
            var attacker = c.GetAttackUnitData() ?? c.GetRecevieUser();
            int who = UnitIndex(attacker, units);
            f.Retal[slot] = who;
            f.PrevAttacker[slot] = who;
            f.RetalCmdUnit[slot] = who;
            f.RetalCmd[slot] = PrCmdToSnes.TryGetValue(c.GetRecevieForAiTypeId(), out int cmd) ? cmd : 0xFF;
            f.RetalAtk[slot] = SnesAttackOf(c.GetRecevieAbilityId());
            f.RetalItm[slot] = 0xFF;
            int elem = 0;
            var attrs = c.GetReceiveAttributeIds();
            if (attrs != null)
                foreach (var a in attrs)
                {
                    int bit = Array.IndexOf(ElemAttr, a);
                    if (bit >= 0) elem |= 1 << bit;
                }
            f.RetalElemUnit[slot] = elem != 0 ? who : -1;
            f.RetalElem[slot] = elem;
            f.RetalFlags[slot] = HitFlags(me, attacker, f.RetalCmd[slot], f.RetalAtk[slot]);
            // a BNW boss at 0 HP: its script's "if HP <= 0" decides, whatever state it's in (BNW's forced death
            // counter), once for each time it reaches 0
            bool atZero = f.AtZero.ContainsKey(uid);
            if (atZero && !f.ZeroCountered.Add(uid)) return false;
            var acts = SnesAi.RunCounter(m, 4 + slot, new View(f, units), !atZero && Unresponsive(me));
            var lines = new List<string>();
            var entries = new List<SnesAiAction>();
            var q = Plan(f, m, snesId, slot, acts, units, lines, entries);
            if (EraseAllIn(entries)) q.Insert(0, (EraseAll, null));
            else
            {
                ApplyEntries(f, slot, snesId, entries);
                if (entries.Count > 0 && AllGone(f)) q.Insert(0, (EraseAll, null));
            }
            Counters++;
            LastCounter = $"monster {snesId} slot {slot} hit by unit {who} (cmd {f.RetalCmd[slot]:x2}, attack {f.RetalAtk[slot]:x2}, elements {elem:x2}" +
                          (f.RetalFlags[slot] != 0 ? $", flags {f.RetalFlags[slot]:x2}" : "") + (atZero ? ", at 0 HP" : "") + $"): {string.Join("; ", acts)} => {Show(q)}";
            if (Verbose) Log(" counter: " + LastCounter);
            if (atZero && q.Count > 0 && q[0].ability == EraseAll) { EraseThroughDeath(f, me); return false; }
            if (q.Count == 0) return false;
            var first = new List<(int, Il2CppReferenceArray<BattleUnitData>)> { q[0] };
            Rewrite(bc, MarkCounter, first, lines, false);
            Plan(me, first);
            return true;
        }

        // PR ability -> SNES attack index (the data's map; Battle/Fight -> EE)
        static int SnesAttackOf(int ability)
        {
            var d = D;
            if (d == null || ability <= 0) return 0xFF;
            if (ability == 1) return 0xEE;
            int i = Array.IndexOf(d.Attacks, ability);
            return i >= 0 ? i : 0xFF;
        }

        // ---- a death --------------------------------------------------------------------------------------
        public static bool FinalAttackAct_Prefix(BattleAICore __0, ref int __result)
        {
            if (Verbose) Hooks.NoteOnce("ai-FinalAttackAct_Prefix", () => $"[{Tag}] enemy AI: first FinalAttackAct row seen (marker {Marker(__0)}, host on {Active})");
            if (!Active || Marker(__0) != MarkFinal) return true;
            try
            {
                var c = Controller(__0);
                if (c == null || !c.IsReadyActFinalAttack()) return true;
                if (Final(__0, c)) return true;     // the PR's FinalAttackAct queues it
                __result = 0;
                return false;                       // just dies
            }
            catch (Exception e) { Hooks.WarnOnce("enemy-ai-final", e); return true; }
        }

        static bool Final(BattleAICore bc, ActSelectEnemyController c)
        {
            var f = EnsureFight();
            var me = Owner(c);
            if (me == null) return false;
            Join(f, me);
            var units = Units();
            int uid = me.UniqueId, slot = f.Slot.TryGetValue(uid, out int s) ? s : 0, snesId = f.SnesId.TryGetValue(uid, out int sid) ? sid : -1;
            if (!f.Monsters.TryGetValue(uid, out var m)) return false;
            if (f.Erased.Contains(uid)) return false;   // "everyone dies like a boss" killed it: no death script
            // a BNW boss held at 0 HP: the PR raises its final-attack flag at 0 HP, but it isn't dying: its
            // counter script runs as it would alive at 0 HP ("if HP <= 0": an extra life, everyone dies like a
            // boss, FB 0B 07), whatever state it's in (found in game 2026-11-19: run as a dying monster, the
            // Guardian's six lives never ran and it sat at 1 HP)
            if (f.EraseOnDeath.Remove(uid))
            {
                // its script ended in "everyone dies like a boss" at 0 HP: the PR's erase-all as its final attack
                Finals++;
                f.EraseAllAt = f.Clock + 12;   // the erase-all plays; anyone still standing after that dies
                LastFinal = $"monster {snesId} slot {slot}: everyone dies like a boss (its script at 0 HP) => {EraseAll}";
                if (Verbose) Log(" death: " + LastFinal);
                var erase = new List<(int, Il2CppReferenceArray<BattleUnitData>)> { (EraseAll, null) };
                Rewrite(bc, MarkFinal, erase, new List<string>(), false);
                Plan(me, erase);
                return true;
            }
            bool held = f.AtZero.ContainsKey(uid);
            if (held && !f.ZeroCountered.Add(uid)) return false;
            var acts = SnesAi.RunCounter(m, 4 + slot, new View(f, units), !held);
            var lines = new List<string>();
            var entries = new List<SnesAiAction>();
            var q = Plan(f, m, snesId, slot, acts, units, lines, entries);
            // "everyone leaves/dies like a boss" (F5 xx 01/05 FF): the PR's erase-all, which ends the fight
            if (held && EraseAllIn(entries)) { EraseThroughDeath(f, me); return false; }
            if (EraseAllIn(entries)) q.Insert(0, (EraseAll, null));
            else if (held)
            {
                ApplyEntries(f, slot, snesId, entries);   // alive: now (a restore refills it)
                if (entries.Count > 0 && AllGone(f)) q.Insert(0, (EraseAll, null));
            }
            else if (entries.Count > 0)
            {
                f.Deferred.Add((me, slot, snesId, entries, f.Clock + 2));   // after its death animation
                // a death that brings others in (Chaser's Trappers, a Flan's halves): the SNES keeps the
                // battle from ending meanwhile (its own F5 mode 4, or the shown monsters counting at once)
                if (entries.Any(e => e.B2 == 0 || e.B2 == 2 || e.B2 == 4)) Hold(f, BattlePlugManager.Instance(), 15);
            }
            Finals++;
            LastFinal = $"monster {snesId} slot {slot}{(held ? " (held at 0 HP)" : "")}: {string.Join("; ", acts)} => {Show(q)}";
            if (Verbose) Log(" death: " + LastFinal);
            if (q.Count == 0) return false;
            var first = new List<(int, Il2CppReferenceArray<BattleUnitData>)> { q[0] };
            Rewrite(bc, MarkFinal, first, lines, false);
            Plan(me, first);
            return true;
        }

        // ---- BNW's bosses at 0 HP ---------------------------------------------------------------------------
        // The PR kills a unit at 0 HP in UpdateConditionHp. A monster the rules say dies only through its
        // script (BNW's boss flag) is held at 1 HP instead, at 0 for its scripts, until its counter script
        // restores it (an extra life: F5 mode 0), ends the fight (F5 xx xx FF), or kills it (FB 0B 07).
        public static bool UpdateConditionHp_Prefix(BattleUnitData __0)
        {
            try
            {
                var d = D;
                if (!Active || d == null || !d.Rules.BossDeathByScript || d.BossDeath.Count == 0) return true;
                var ed = __0?.TryCast<BattleEnemyData>();
                var p = ed?.BattleUnitDataInfo?.Parameter;
                if (p == null || p.CurrentHP > 0) return true;
                var f = Current;
                if (f == null || fightSerial != battleSerial) f = EnsureFight();
                Join(f, ed);
                int uid = ed.UniqueId;
                if (f.Dying.Contains(uid) || !f.Monsters.ContainsKey(uid)) return true;
                if (!f.SnesId.TryGetValue(uid, out int id) || !d.BossDeath.Contains(id) || !d.Plugin.Contains(id)) return true;
                p.CurrentHP = 1;
                if (!f.AtZero.ContainsKey(uid))
                {
                    f.AtZero[uid] = f.Clock;
                    f.ZeroCountered.Remove(uid);
                    f.ZeroForced.Remove(uid);
                    if (Verbose) Log($": monster {id} (unique {uid}) at 0 HP: its script decides");
                }
                return false;
            }
            catch (Exception e) { Hooks.WarnOnce("enemy-ai-zero-hp", e); return true; }
        }

        // the game's own death for a monster its script killed (FB 0B 07), or one held at 0 HP for too long
        static void Kill(Fight f, BattleEnemyData ed, string why)
        {
            var p = ed?.BattleUnitDataInfo?.Parameter;
            if (p == null) return;
            f.Dying.Add(ed.UniqueId);
            f.AtZero.Remove(ed.UniqueId);
            p.CurrentHP = 0;
            try
            {
                var ctrl = BattlePlugManager.Instance()?.BattleConditionController;
                if (ctrl != null && updateConditionHp != null) updateConditionHp.Invoke(ctrl, new object[] { ed });
            }
            catch (Exception e) { Hooks.WarnOnce("enemy-ai-kill", e); }
            if (Verbose) Log($": unique {ed.UniqueId} dies ({why})");
        }

        // a held BNW boss's counter section run by the host (no AI pass asked for it): variables, entries
        // (a restore) at once, its first act (an attack, or everyone dying like a boss) queued as a counter
        static void DeathScript(Fight f, BattleEnemyData ed)
        {
            int uid = ed.UniqueId;
            if (!f.Monsters.TryGetValue(uid, out var m)) return;
            int slot = f.Slot.TryGetValue(uid, out int s) ? s : 0, snesId = f.SnesId.TryGetValue(uid, out int sid) ? sid : -1;
            var units = Units();
            var acts = SnesAi.RunCounter(m, 4 + slot, new View(f, units), false);
            var lines = new List<string>();
            var entries = new List<SnesAiAction>();
            var q = Plan(f, m, snesId, slot, acts, units, lines, entries);
            if (EraseAllIn(entries)) q.Insert(0, (EraseAll, null));
            else
            {
                ApplyEntries(f, slot, snesId, entries);
                if (entries.Count > 0 && AllGone(f)) q.Insert(0, (EraseAll, null));
            }
            Counters++;
            LastCounter = $"monster {snesId} slot {slot} at 0 HP, run by the host: {string.Join("; ", acts)} => {Show(q)}";
            if (Verbose) Log(" counter: " + LastCounter);
            // its acts aren't queued (an act queued outside an AI pass never finishes: 2026-11-19); everyone
            // dying like a boss goes through its death (the erase-all as its final attack)
            if (q.Count > 0 && q[0].ability == EraseAll) EraseThroughDeath(f, ed);
        }

        // A held boss's "everyone dies like a boss": the PR's erase-all, queued as a counter by a living
        // monster, stages and never finishes (in game 2026-11-19, the Guardian's sixth death); as a final
        // attack it works (BNW's compiled bosses, WC's deaths). So the boss dies the game's way next frame
        // and its final-attack pass queues the erase-all.
        static void EraseThroughDeath(Fight f, BattleEnemyData ed)
        {
            if (ed == null) return;
            f.EraseOnDeath.Add(ed.UniqueId);
            f.PendingKill.Add(ed.UniqueId);
        }

        // tests: a monster brought to 0 HP and the game's own death check on it (what a killing hit ends in)
        public static string KnockOut(BattleEnemyData ed)
        {
            var p = ed?.BattleUnitDataInfo?.Parameter;
            var ctrl = BattlePlugManager.Instance()?.BattleConditionController;
            if (p == null || ctrl == null || updateConditionHp == null) return "no unit / condition controller";
            p.CurrentHP = 0;
            updateConditionHp.Invoke(ctrl, new object[] { ed });
            return $"unique {ed.UniqueId}: HP {p.CurrentHP}" + (Current != null && Current.AtZero.ContainsKey(ed.UniqueId) ? " (held at 0 for its script)" : "");
        }

        // held too long (a script that never handles 0 HP, or no counter came): BNW would wait for good; a
        // stuck fight is worse, so after a minute of battle time the game's death takes over
        const double ZeroHpLimit = 60;

        static void ZeroHpChecks(Fight f)
        {
            if (f.AtZero.Count == 0 && f.PendingKill.Count == 0 && f.EraseAllAt <= 0) return;
            var bm = BattlePlugManager.Instance();
            if (bm == null) return;
            // "everyone dies like a boss" whose erase-all never came: the boss's own death may raise no final
            // attack (the PR raised it at 0 HP already, while the boss was held: a party's killing hit), and
            // then the rest stood there for good (the user's Deck, 2026-11-20: BNW's Whelk, the head dead,
            // the shell idling). Each one left dies the game's own way.
            if (f.EraseAllAt > 0 && f.Clock >= f.EraseAllAt)
            {
                f.EraseAllAt = 0;
                foreach (var e in bm.GetEnemyUnits())
                {
                    var ed = e?.TryCast<BattleEnemyData>();
                    var p = ed?.BattleUnitDataInfo?.Parameter;
                    if (ed == null || p == null || p.CurrentHP <= 0 || f.Dying.Contains(ed.UniqueId)) continue;
                    f.Erased.Add(ed.UniqueId);
                    Log($": unique {ed.UniqueId} dies: everyone dies like a boss (no erase-all came)");
                    Kill(f, ed, "everyone dies like a boss");
                }
            }
            foreach (var e in bm.GetEnemyUnits())
            {
                var ed = e?.TryCast<BattleEnemyData>();
                if (ed == null) continue;
                if (f.PendingKill.Remove(ed.UniqueId))
                {
                    bool erase = f.EraseOnDeath.Contains(ed.UniqueId);
                    Kill(f, ed, erase ? "its script at 0 HP: everyone dies like a boss" : "its script: FB 0B 07");
                    if (erase) f.EraseAllAt = Math.Max(f.EraseAllAt, f.Clock + 2);   // the erase-all, as its final attack
                    continue;
                }
                if (!f.AtZero.TryGetValue(ed.UniqueId, out double since)) continue;
                var p = ed.BattleUnitDataInfo?.Parameter;
                if (p == null) continue;
                if (p.CurrentHP > 1) { f.AtZero.Remove(ed.UniqueId); continue; }   // healed
                // no counter or final-attack pass ran its death script (HP lost to poison or a counterattack, a
                // sleeping boss, or the PR no longer raising those flags for it): BNW still runs it
                // (c2.asm DeathCounterFix), so the host runs it here and queues its act as a counter
                if (!f.ZeroCountered.Contains(ed.UniqueId) && !f.ZeroForced.Contains(ed.UniqueId) && f.Clock - since > 1.0)
                {
                    // first the game's own counter pass (the act it queues plays out right): its counter
                    // count reset (the PR stops a monster's counters at a maximum) and its ready flag raised
                    f.ZeroForced.Add(ed.UniqueId);
                    try
                    {
                        var c = ed.BattleUnitDataInfo?.ActSelectController?.TryCast<ActSelectEnemyController>();
                        c?.ResetCounterCount();
                        var ase = c?.ActSelect?.TryCast<ActSelectEnemy>();
                        if (ase != null) ase.isReadyActCounter = true;
                        if (Verbose) Log($": unique {ed.UniqueId} at 0 HP without a counter: its counter pass asked for");
                    }
                    catch (Exception ex) { Hooks.WarnOnce("enemy-ai-force-counter", ex); }
                }
                else if (!f.ZeroCountered.Contains(ed.UniqueId) && f.Clock - since > 3.0)
                {
                    f.ZeroCountered.Add(ed.UniqueId);
                    try { DeathScript(f, ed); }
                    catch (Exception ex) { Hooks.WarnOnce("enemy-ai-death-script", ex); }
                }
                if (f.Clock - since > ZeroHpLimit) Kill(f, ed, "held at 0 HP for a minute");
            }
        }
    }
}
