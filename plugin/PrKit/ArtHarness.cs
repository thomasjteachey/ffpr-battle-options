using System;
using System.Collections.Generic;
using Last.Management;
using UnityEngine;
using UnityEngine.U2D;

namespace PrKit
{
    // ArtSwap's harness command, for any mod serving art with it (tools/game_harness.py):
    //   art                              the served folder, how many sprites/atlas calls it answered
    //   art load GROUP KEY               load a sprite as the game does: whose it is (the mod's or the game's)
    //   art atlas GROUP ATLAS FRAME      a field atlas's frame, the same
    // A mod registers it under its own name, naming its art in the answers: ArtHarness.Register("bcart", "the seed's").
    public static class ArtHarness
    {
        public static void Register(string name = "art", string whose = "the mod's")
        {
            mine = whose;
            Harness.Register(name, Run, $"{name} [load GROUP KEY | atlas GROUP ATLAS FRAME | keys TEXT | ui TEXT]: the art ArtSwap serves; inspect loaded resources and live UI images");
        }

        static string group, key, frame, mine = "the mod's";
        static bool atlas;

        static string Run(string[] a, int tick)
        {
            if (a.Length == 0) return $"{ArtSwap.Last}; served {ArtSwap.Served} sprites, {ArtSwap.AtlasServed} atlas calls";
            var rm = ResourceManager.Instance;
            if (rm == null) return "error: no ResourceManager";
            if (a[0] == "keys" && a.Length > 1)
            {
                var hits = new List<string>();
                var dic = rm.completeAssetDic;
                if (dic != null)
                    foreach (var k in dic.Keys)
                        if (k != null && k.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hits.Add(k);
                            if (hits.Count == 12) break;
                        }
                return $"loaded addresses matching {a[1]}: {(hits.Count == 0 ? "none" : string.Join(" | ", hits))}";
            }
            if (a[0] == "ui" && a.Length > 1)
            {
                var hits = new List<string>();
                foreach (var image in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Image>())
                {
                    var sprite = image?.sprite;
                    if (sprite == null || sprite.name == null || sprite.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hits.Add($"{image.name}: {Describe(sprite)}");
                    if (hits.Count == 12) break;
                }
                return $"UI images matching {a[1]}: {(hits.Count == 0 ? "none" : string.Join(" | ", hits))}";
            }
            if (tick == 0)
            {
                atlas = a[0] == "atlas";
                if ((a[0] == "load" && a.Length >= 3) || (atlas && a.Length >= 4))
                {
                    group = a[1]; key = a[2]; frame = atlas ? a[3] : null;
                    if (atlas) rm.RequestLoadAssetBundle<SpriteAtlas>(group, key, false);
                    else rm.RequestLoadAssetBundle<Sprite>(group, key, false);
                    return null;
                }
                return "error: [load GROUP KEY | atlas GROUP ATLAS FRAME]";
            }
            bool done = rm.CheckLoadAssetCompleted(group, key);
            if (!done && tick < 600) return null;
            if (!done) return $"{group}/{key} not loaded";
            try
            {
                if (atlas)
                {
                    var at = rm.GetAssetData<SpriteAtlas>(group, key);
                    if (at == null) return $"{group}/{key} loaded, no atlas";
                    var sp = at.GetSprite(frame);
                    if (sp == null) return $"{group}/{key}: atlas {at.name} ({at.spriteCount} sprites) has no {frame}";
                    return $"{group}/{key} atlas {at.name} ({at.spriteCount} sprites): {Describe(sp)}";
                }
                var s = rm.GetAssetData<Sprite>(group, key);
                return s == null ? $"{group}/{key} loaded, no sprite" : $"{group}/{key}: {Describe(s)}";
            }
            catch (Exception e) { return $"error: {e.GetType().Name}: {e.Message}"; }
        }

        // ArtSwap names its textures after their PNG files; the game's are named after the asset
        static string Describe(Sprite sp)
        {
            var tex = sp.texture;
            var ours = tex != null && tex.name != null && tex.name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            return $"sprite {sp.name} {sp.rect.width}x{sp.rect.height} pivot {sp.pivot.x / Math.Max(1, sp.rect.width):0.##},{sp.pivot.y / Math.Max(1, sp.rect.height):0.##} " +
                   $"texture {tex?.name} {tex?.width}x{tex?.height} ({(ours ? mine : "the game's")})";
        }
    }
}
