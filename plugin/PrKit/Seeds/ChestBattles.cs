using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Interpreter;
using Last.Interpreter.Instructions;

namespace PrKit.Seeds
{
    // Chests a seed made monster-in-a-box (Beyond Chaos's treasure randomizer, Worlds Collide's -chrm):
    // a PR chest can only start a battle through a script of its own, and a live seed can't add one
    // (Magicite only serves files that existed at startup), so the seed's json lists them (tools/
    // seed_info.monster_chests: PR chest flag -> the party it fights) and the plugin empties the chest as
    // it opens ("Monster-in-a-box!") and starts that battle once the field is free: EncountBoss, as a
    // script would run it. Worlds Collide's WcItems/WcBattles (2026-11-06), in the kit since 2026-11-20.
    public static class ChestBattles
    {
        public static Func<IReadOnlyDictionary<int, int>> Chests = () => null;   // the active seed's (null: none)
        public static string Tag = "[kit]";
        public static string LastChest = "", LastBattle = "";
        public static int Started;

        static int queued;
        static float queuedAt;

        public static void Patch(Harmony h)
        {
            var open = AccessTools.Method(typeof(global::Last.Map.EventAction), "OpenTreasureBox");
            if (open != null) h.Patch(open, prefix: new HarmonyMethod(typeof(ChestBattles), nameof(OpenChest_Prefix)));
            Kit.Info($"{Tag} monster-in-a-box chests: {(open != null ? "hooked" : "EventAction.OpenTreasureBox not found")}");
        }

        // EventAction.OpenTreasureBox: the coroutine reads the chest's property later, so emptying it
        // here gives the "Monster-in-a-box!" line and nothing else
        public static void OpenChest_Prefix(global::Last.Entity.Field.FieldTresureBox entity)
        {
            try
            {
                var chests = Chests();
                if (chests == null || chests.Count == 0 || entity == null || entity.isOpen) return;
                var p = entity.tresureBoxProperty;
                if (p == null || global::Last.Map.MapUIManager.GetTreasureFlag(p.FlagId)) return;
                if (!chests.TryGetValue(p.FlagId, out int party)) return;
                p.ContentId = 1;
                p.ContentNum = 0;
                p.MessageKey = "T0001_04_01";   // "Monster-in-a-box!" (the chest's own key is its old contents')
                Queue(party);
                LastChest = $"chest {p.FlagId}: monster-in-a-box, party {party}";
                Kit.Info($"{Tag} {LastChest}");
            }
            catch (Exception e) { Hooks.WarnOnce("chest-battle", e); }
        }

        public static void Queue(int party)
        {
            queued = party;
            queuedAt = UnityEngine.Time.realtimeSinceStartup;
        }

        static float nextCheck;

        // every frame (the mod's runner): the queued battle once no event or battle runs
        public static void Update()
        {
            if (queued <= 0) return;
            var t = UnityEngine.Time.realtimeSinceStartup;
            if (t < nextCheck) return;
            nextCheck = t + 0.25f;
            if (t - queuedAt > 60) { queued = 0; Kit.Info($"{Tag} queued battle dropped (the field stayed busy)"); return; }
            try
            {
                var b = global::Last.Battle.BattlePlugManager.Instance();
                bool busy = Integrator.Instance?.IsMainCoreAlive() ?? false;
                if ((b != null && b.IsBattle()) || busy || UnityEngine.Object.FindObjectOfType<global::FieldMap>() == null || t - queuedAt < 1.5f)
                    return;
                int party = queued;
                queued = 0;
                Start(party);
            }
            catch (Exception e) { queued = 0; Hooks.WarnOnce("chest-battle-start", e); }
        }

        // EncountBoss with this party, as a script runs it
        public static void Start(int party)
        {
            var mc = new MainCore();
            var op = new global::Last.Interpreter.Instructions.Format.Operands();
            op.iValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(8);
            op.rValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<double>(8);
            op.sValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(8);
            for (int k = 0; k < 8; k++) op.sValues[k] = "";
            op.iValues[0] = party;
            var mn = new global::Last.Interpreter.Instructions.Format.Mnemonic();
            mn.mnemonic = "EncountBoss";
            mn.operands = op;
            mc.currentInstruction = mn;
            global::Last.Interpreter.Instructions.External.Misc.EncountBoss(mc);
            Started++;
            LastBattle = $"monster-in-a-box: party {party}";
            Kit.Info($"{Tag} {LastBattle}");
        }
    }
}
