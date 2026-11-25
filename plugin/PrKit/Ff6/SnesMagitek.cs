using System;
using HarmonyLib;

namespace PrKit.Ff6
{
    // MagiTek as an ordinary command, for any FF6 mod that deals it (2026-11-20, from Worlds Collide's
    // WcMagitek, for Beyond Chaos: its command shuffle can deal MagiTek, and its `metronome` code turns
    // the MagiTek command into Random Chaos for everyone). The PR's Magitek (command 26) is the armor's:
    // listed and usable only in Magitek armor (release_type 3, condition 53; CommandAvailableData
    // special-cases it), and every character keeps it as a hidden slot. For a character the mod names
    // (Dealt), it is listed and usable anywhere: the armor's beams they own, or whatever the mod's
    // command row 26 now runs. Opt-in: the mod sets Dealt and calls Patch once.
    public static class SnesMagitek
    {
        public const int Command = 26;
        public static Func<int, bool> Dealt = _ => false;      // character_status id -> MagiTek listed and usable anywhere
        public static string Tag = "[kit]";
        static bool patched;

        public static void Patch(Harmony h)
        {
            if (patched) return;
            patched = true;
            var t = typeof(Serial.FF6.Management.CommandAvailableData);
            int n = 0;
            foreach (var (name, post) in new[] { ("IsCommandRelease", nameof(Release_Postfix)), ("IsAvailable", nameof(Available_Postfix)) })
                if (Hooks.Patch(h, t, name, typeof(SnesMagitek), postfix: post)) n++;
            Kit.Info($"{Tag} MagiTek as a command: {n} of 2 command checks hooked");
        }

        static bool Allow(Last.Data.User.OwnedCharacterData who, Last.Data.Master.Command command) =>
            command != null && command.Id == Command && who != null && Dealt(who.characterStatusId);

        public static void Release_Postfix(Last.Data.User.OwnedCharacterData targetData, Last.Data.Master.Command command, ref bool __result)
        {
            try { if (!__result && Allow(targetData, command)) __result = true; }
            catch (Exception e) { Hooks.WarnOnce("kit-magitek-release", e); }
        }

        public static void Available_Postfix(Last.Data.User.OwnedCharacterData targetData, Last.Data.Master.Command command, ref bool __result)
        {
            try { if (!__result && Allow(targetData, command)) __result = true; }
            catch (Exception e) { Hooks.WarnOnce("kit-magitek-available", e); }
        }
    }
}
