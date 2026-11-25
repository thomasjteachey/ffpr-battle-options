using System.Collections.Generic;
using System.Linq;

namespace PrKit.Ff6
{
    // FF6's rages as the PR numbers them (game-free; tests: PrKit.Tests). A rage is the PR's ability 799 + PR monster id
    // (the PR's monster = the SNES enemy + 1): the SNES's 255 rage-able enemies are PR monsters 1..255, abilities
    // 800..1054 (StartSkills.Sets). SnesRages teaches them; this says which.
    public static class SnesRagesRules
    {
        public const int RageBase = 799, FirstMonster = 1, LastMonster = 255;

        // a PR monster's rage ability, 0 for a monster that has none
        public static int RageAbility(int prMonster) => prMonster >= FirstMonster && prMonster <= LastMonster ? RageBase + prMonster : 0;

        // a battle's monsters' rages, in order, each once
        public static List<int> Rages(IEnumerable<int> prMonsters) =>
            (prMonsters ?? Enumerable.Empty<int>()).Select(RageAbility).Where(a => a > 0).Distinct().ToList();
    }
}
