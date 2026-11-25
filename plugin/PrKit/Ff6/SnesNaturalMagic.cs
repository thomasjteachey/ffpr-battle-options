using System;
using System.Collections.Generic;
using System.Linq;
using Last.Data.Master;
using Last.Data.User;
using Last.Management;

namespace PrKit.Ff6
{
    // Natural magic when a mod moves it. In the SNES a character learns spells from its natural-magic list as it joins
    // and levels; a randomizer that gives the lists to others (Worlds Collide -nm1/-nm2, Beyond Chaos) has the kit's
    // port write them as type-1 learning rows on each learner's job (port_spells). But the PR also builds spells into
    // its characters outside that table: Terra is created knowing Cure and Fire, Celes Blizzard (BuiltIn). Recruit
    // (a first recruit, at its final level) leaves a character knowing its natural magic up to that level and what the
    // mod grants everyone, nothing else; RepairBuiltIns un-learns a built-in spell nothing in the seed teaches (saves
    // recruited before a fix). From Worlds Collide's WcSpells (2026-11-21).
    public static class SnesNaturalMagic
    {
        public const int First = 31, Last = 84;   // the PR's spells (white 31-39, black 40-63, gray 64-84)
        public static string Tag = "[kit]";

        // the spells the PR builds into a character (status id) outside its learning rows
        public static readonly Dictionary<int, int[]> BuiltIn = new Dictionary<int, int[]>
        {
            [1] = new[] { 31, 40 },   // Terra: Cure, Fire
            [7] = new[] { 41 },       // Celes: Blizzard
        };

        public static bool IsSpell(int id) => id >= First && id <= Last;

        static bool Known(OwnedAbility a) => a?.Ability != null && IsSpell(a.Ability.Id) && a.SkillLevel > 0;

        // a character's natural magic up to `level`: the type-1 learning rows on its job (spells)
        public static HashSet<int> Natural(OwnedCharacterData c, int level) => Natural(c, level, out _, out _);

        // ... and whether it has such rows at all, and a Cure row among them
        public static HashSet<int> Natural(OwnedCharacterData c, int level, out bool learner, out bool cureRow)
        {
            var set = new HashSet<int>();
            learner = cureRow = false;
            var mm = MasterManager.Instance;
            if (c == null || mm == null) return set;
            foreach (var kv in mm.GetList<Learning>())
            {
                var l = kv.Value;
                if (l == null || l.TypeId != 1 || l.JobId != c.JobId) continue;
                var content = mm.GetData<Content>(l.ContentId);
                if (content == null || content.TypeId != 4 || !IsSpell(content.TypeValue)) continue;
                learner = true;
                if (content.TypeValue == 31) cureRow = true;
                if (l.Value1 <= level) set.Add(content.TypeValue);
            }
            return set;
        }

        // a first recruit at `level`: knows `natural` (and keeps `granted`), no other spells
        public static string Recruit(OwnedCharacterData c, int level, HashSet<int> natural, HashSet<int> granted)
        {
            if (c == null || c.OwnedAbilityList == null) return "";
            var keep = new HashSet<int>(natural);
            if (granted != null) keep.UnionWith(granted);
            var dropped = new List<int>();
            foreach (var a in c.OwnedAbilityList)
                if (Known(a) && !keep.Contains(a.Ability.Id)) { a.SkillLevel = 0; dropped.Add(a.Ability.Id); }
            var taught = new List<int>();
            foreach (var id in natural.OrderBy(x => x))
                if (!StartSkills.Knows(c, id) && StartSkills.Teach(c, id)) taught.Add(id);
            if (dropped.Count == 0 && taught.Count == 0) return "";
            var what = $"character {c.characterStatusId} at level {level}: spells un-learned [{string.Join(" ", dropped)}] (not its natural magic), learned [{string.Join(" ", taught)}]";
            Kit.Info($"{Tag} natural magic: " + what);
            return what;
        }

        // Owned characters already recruited (isRecruited): a built-in spell nothing could have taught goes. Kept when
        // its natural magic gives it by its level (natural), an owned esper or any gear teaches it, or the mod grants it
        // (granted, asked only when needed): the PR doesn't record where a spell came from.
        public static string RepairBuiltIns(Func<int, bool> isRecruited, Func<OwnedCharacterData, HashSet<int>> natural,
                                            Func<HashSet<int>> granted)
        {
            var udm = UserDataManager.Instance();
            var owned = udm?.OwnedCharacterList;
            if (owned == null) return "";
            var mm = MasterManager.Instance;
            HashSet<int> espers = null, gear = null, grant = null;
            var done = new List<string>();
            foreach (var c in owned)
            {
                if (c == null || !BuiltIn.TryGetValue(c.characterStatusId, out var builtIn) || !isRecruited(c.characterStatusId) ||
                    c.OwnedAbilityList == null) continue;
                HashSet<int> nat = null;
                foreach (var a in c.OwnedAbilityList)
                {
                    if (!Known(a) || !builtIn.Contains(a.Ability.Id)) continue;
                    if (espers == null)
                    {
                        espers = new HashSet<int>(); gear = new HashSet<int>();
                        var stones = udm.OwnedMagicStoneList;
                        foreach (var kv in mm.GetList<Learning>())
                        {
                            var l = kv.Value;
                            if (l == null || (l.TypeId != 2 && l.TypeId != 4)) continue;
                            var content = mm.GetData<Content>(l.ContentId);
                            if (content == null || content.TypeId != 4) continue;
                            if (l.TypeId == 4) gear.Add(content.TypeValue);
                            else if (stones != null && stones.Contains(l.Value1)) espers.Add(content.TypeValue);
                        }
                        grant = granted?.Invoke() ?? new HashSet<int>();
                    }
                    nat ??= natural(c);
                    int id = a.Ability.Id;
                    if (nat.Contains(id) || espers.Contains(id) || gear.Contains(id) || grant.Contains(id)) continue;
                    a.SkillLevel = 0;
                    done.Add($"{c.characterStatusId}:{id}");
                }
            }
            if (done.Count == 0) return "";
            var what = string.Join(" ", done);
            Kit.Info($"{Tag} natural magic: built-in spells nothing in the seed teaches un-learned ({what})");
            return what;
        }
    }
}
