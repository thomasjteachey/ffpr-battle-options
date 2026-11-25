using System;
using System.Collections.Generic;
using System.Linq;
using Last.Data.User;
using Last.Management;

namespace PrKit.Ff6
{
    // FF6's technique sets (SwdTech, Blitz, Lore, Dance, Rage) known from the start or learned on the party's behalf.
    // In the SNES they are the party's: whoever holds the command uses the party's set. In the PR they are each
    // character's own abilities, learned to a skill level (100 = known). A mod that gives skills outside the game's own
    // learning (a randomizer's start skills, rages won while nobody holds Rage) teaches them to the command's holders:
    // Teach(command, abilities). Abilities for a command nobody holds yet wait in a queue (this session only) until
    // TeachPending() finds a holder (call it on joins / the field's sync). Opt-in: nothing runs unless a mod calls it.
    // Generalized from Worlds Collide's WcRages/WcSkills (2026-11-20, the kit lead's OK; WC moves onto it later).
    public static class StartSkills
    {
        // the PR's command ids
        public const int Bushido = 12, Blitz = 15, Lore = 17, Dance = 22, Rage = 23;

        // (command, first ability, last ability): the PR's abilities of each set (Rage: 799 + PR monster id)
        public static readonly (int Command, int First, int Last)[] Sets =
        {
            (Bushido, 124, 131), (Blitz, 132, 139), (Lore, 140, 163), (Dance, 164, 171), (Rage, 800, 1054),
        };

        public static string Tag = "[kit]";
        public static Action<string, Exception> Warn = Hooks.WarnOnce;   // a mod's own warning (WC: WcRunner.WarnOnce)
        public static string Last = "";
        static readonly Dictionary<int, HashSet<int>> pending = new Dictionary<int, HashSet<int>>();

        // the playable characters (status 1 Terra .. 14 Umaro) the player owns
        public static IEnumerable<OwnedCharacterData> Owned()
        {
            var list = UserDataManager.Instance()?.OwnedCharacterList;
            if (list == null) yield break;
            foreach (var c in list)
                if (c != null && c.characterStatusId >= 1 && c.characterStatusId <= 14) yield return c;
        }

        public static bool Has(OwnedCharacterData c, int command)
        {
            var list = c?.CommandList;
            if (list == null) return false;
            foreach (var x in list) if (x != null && x.Id == command) return true;
            return false;
        }

        // the owned characters who hold a command
        public static List<OwnedCharacterData> Holders(int command) => Owned().Where(c => Has(c, command)).ToList();

        // known = learned to 100 (the PR gives Gau an entry for every rage, the unknown ones at 0)
        public static bool Knows(OwnedCharacterData c, int ability)
        {
            var list = c?.OwnedAbilityList;
            if (list == null) return false;
            foreach (var a in list)
                if (a?.Ability != null && a.Ability.Id == ability) return a.SkillLevel >= 100;
            return false;
        }

        // one character learns one ability to 100 (an entry added if it has none)
        public static bool Teach(OwnedCharacterData c, int ability)
        {
            if (c == null) return false;
            OwnedAbility owned = null;
            if (c.OwnedAbilityList != null)
                foreach (var a in c.OwnedAbilityList)
                    if (a?.Ability != null && a.Ability.Id == ability) owned = a;
            owned ??= new OwnedCharacterClient().AddAbility(c.Id, ability);
            if (owned == null) return false;
            owned.SkillLevel = 100;
            return true;
        }

        // every holder of `command` learns `abilities` (those not known yet); with no holder they wait (TeachPending)
        // unless queue is false (a seed's start skills, read again on every call: a queue would outlive a seed switch).
        // Returns how many were taught.
        public static int Teach(int command, IEnumerable<int> abilities, bool queue = true)
        {
            var ids = abilities.Distinct().ToList();
            var holders = Holders(command);
            if (holders.Count == 0)
            {
                if (!queue) return 0;
                if (!pending.TryGetValue(command, out var wait)) pending[command] = wait = new HashSet<int>();
                foreach (var id in ids) wait.Add(id);
                return 0;
            }
            int n = 0;
            foreach (var c in holders)
                foreach (var id in ids)
                    if (!Knows(c, id) && Teach(c, id)) n++;
            if (n > 0) Note($"command {command}: {n} taught to {string.Join(",", holders.Select(h => h.characterStatusId))}");
            return n;
        }

        // the queued abilities to the commands' holders, once someone holds them
        public static int TeachPending()
        {
            int n = 0;
            foreach (var command in pending.Keys.ToList())
            {
                if (Holders(command).Count == 0) continue;
                var ids = pending[command].ToList();
                n += Teach(command, ids);
                pending.Remove(command);          // only once taught (a throw keeps them queued)
            }
            return n;
        }

        public static int Pending(int command) => pending.TryGetValue(command, out var s) ? s.Count : 0;

        // the queued abilities of a command, taken out of the queue (a mod that keeps them its own way: BC teaches them
        // to an owned character, so the save keeps them and Sync shares them with a later holder)
        public static List<int> TakePending(int command)
        {
            if (!pending.TryGetValue(command, out var s)) return new List<int>();
            pending.Remove(command);
            return s.ToList();
        }

        // The SNES's party-wide sets: every holder of a set's command knows what any owned character knows of that set
        // (taught to 100). For a mod that moves commands (WC, BC's `o`): call it on joins and every few seconds in the
        // field. Returns what was taught ("" when nothing). From Worlds Collide's WcSkills.Sync (2026-11-21).
        public static string LastSync = "";

        public static string Sync() => Sync(null);

        // only these commands' sets (null: every set)
        public static string Sync(ICollection<int> commands)
        {
            var report = new List<string>();
            try
            {
                var owned = Owned().ToList();
                foreach (var (command, first, last) in Sets)
                {
                    if (commands != null && !commands.Contains(command)) continue;
                    var holders = owned.Where(c => Has(c, command)).ToList();
                    if (holders.Count == 0) continue;
                    var known = new HashSet<int>();
                    foreach (var c in owned)
                        if (c.OwnedAbilityList != null)
                            foreach (var a in c.OwnedAbilityList)
                                if (a?.Ability != null && a.Ability.Id >= first && a.Ability.Id <= last && a.SkillLevel >= 100)
                                    known.Add(a.Ability.Id);
                    int taught = 0;
                    foreach (var h in holders)
                        foreach (var id in known)
                            if (!Knows(h, id) && Teach(h, id)) taught++;
                    if (taught > 0) report.Add($"command {command}: {taught} taught to {string.Join(",", holders.Select(h => h.characterStatusId))}");
                }
            }
            catch (Exception e) { Warn("skills-sync", e); }
            if (report.Count == 0) return "";
            LastSync = string.Join("; ", report);
            Kit.Info($"{Tag} skills shared: {LastSync}");
            return LastSync;
        }

        static void Note(string what)
        {
            Last = what;
            Kit.Info($"{Tag} skills: {what}");
        }
    }
}
