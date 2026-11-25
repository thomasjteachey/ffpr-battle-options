using System.Collections.Generic;
using System.Linq;

namespace PrKit.Ff6
{
    // Which monsters Suplex can't hit, game-free (SnesSuplex.cs applies it in battle; PrKit.Tests checks this).
    // SNES: monster byte $13 bit 2 ("can't suplex") makes Suplex miss. VanillaNoSuplex is vanilla's list as PR
    // monster ids (SNES monster + 1): 159 of the 384 slots, 146 of them named (Whisper, Vulture, Chupon, the
    // big bosses...) and 13 unnamed slots past 365. A mod that changes the bit reads its own list from its ROM
    // ([idmap.monster(e.id) for e in ff6snes.enemies(rom) if e.raw[19] & 4]; tests/test_snes_rules.py checks this
    // list against the US ROM that way).
    // The PR's Coliseum fights copies of monsters (ids 500+, each with its regular's name key and stats), so a
    // list of regulars alone lets Suplex land on a flagged monster there: VanillaNoSuplexCopies are the copies of
    // VanillaNoSuplex's monsters (tools/port.py monster_copies: same name key, HP, level, attack, defense in the
    // PR's vanilla monster table; tests/test_snes_rules.py checks it), and VanillaWithCopies both. A mod with its
    // own list adds its copies the same way (BNW: bnw_data.json's no_suplex).
    public static class SnesSuplexRules
    {
        public const int SuplexAbility = 134;   // the PR's Suplex (Blitz, ability group 4, function 2)

        public static readonly int[] VanillaNoSuplex =
        {
            11, 13, 15, 19, 35, 36, 38, 41, 43, 44, 46, 47, 48, 51, 54, 63, 65, 68, 69, 71, 73, 75, 80, 81, 82, 87,
            90, 91, 97, 99, 108, 110, 111, 115, 130, 131, 135, 137, 138, 140, 141, 142, 145, 150, 156, 161, 163, 165,
            171, 173, 178, 182, 183, 185, 190, 192, 193, 202, 205, 208, 209, 214, 217, 219, 221, 223, 228, 230, 231,
            233, 237, 239, 242, 248, 251, 252, 257, 258, 259, 260, 261, 265, 266, 268, 269, 270, 271, 274, 276, 277,
            278, 279, 280, 283, 284, 286, 287, 296, 297, 298, 299, 301, 302, 303, 304, 306, 307, 309, 310, 311, 312,
            314, 316, 317, 318, 319, 320, 321, 322, 323, 325, 326, 327, 328, 329, 331, 332, 338, 341, 342, 344, 345,
            346, 347, 348, 349, 350, 351, 352, 357, 359, 361, 362, 367, 368, 369, 370, 371, 372, 373, 376, 377, 378,
            379, 380, 381, 382, 383, 384,
        };

        // the Coliseum copies (PR ids 500+) of VanillaNoSuplex's monsters: 18
        public static readonly int[] VanillaNoSuplexCopies =
        {
            500, 503, 504, 511, 518, 523, 526, 535, 540, 544, 546, 551, 552, 553, 554, 555, 590, 591,
        };

        public static readonly int[] VanillaWithCopies = VanillaNoSuplex.Concat(VanillaNoSuplexCopies).ToArray();

        // the act misses: it is the mod's Suplex and the target is a monster on the mod's list
        public static bool Misses(int abilityId, int monsterId, ICollection<int> noSuplex, int suplexAbility = SuplexAbility)
            => abilityId == suplexAbility && monsterId > 0 && noSuplex != null && noSuplex.Contains(monsterId);
    }
}
