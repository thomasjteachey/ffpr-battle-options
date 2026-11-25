using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PrKit
{
    // ArtSwap's "new_groups" (2026-11-21, T-Edition's own NPC sprites): a field NPC group the game never shipped,
    // made from one it has. The game draws an NPC from its group's SpriteAtlas and can't be handed a new atlas
    // (Unity has no public way to pack one at run time), so the new group borrows one:
    //   - its AssetBundlePathMatch keys are `like`'s (anim_info: the same frame names; each frame's address);
    //   - its atlas key (the group's name in capitals, as the game's own groups have it) points at an address of
    //     its own, "<like's atlas address with like's name replaced by the group's>";
    //   - `like`'s atlas key is added to the group too, so loading the group loads `like`'s atlas;
    //   - when the game asks for the new address, ArtSwap answers a copy of `like`'s loaded atlas renamed to the
    //     group's atlas name, so its `atlases` entry under that name (the mod's frames) answers GetSprite for the
    //     copy only: the vanilla NPC that shares the frame names keeps its own atlas.
    // art.json: {"new_groups": {group: {"like": an existing NPC group, "atlas": name (default: group in capitals)}},
    //            "atlases": {that atlas name: {frame: {png, rect, pivot}}}, ...} and the mod's mapobject rows point
    //            at the group. Game-free here (PrKit.Tests); ArtSwap does the game side.
    public static class ArtNewGroups
    {
        public sealed class Group
        {
            public string Name, Like, Atlas;
        }

        // the "new_groups" object of an art.json root (none: empty)
        public static List<Group> Parse(JsonElement root)
        {
            var out_ = new List<Group>();
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("new_groups", out var ng) ||
                ng.ValueKind != JsonValueKind.Object) return out_;
            foreach (var p in ng.EnumerateObject())
            {
                if (!p.Value.TryGetProperty("like", out var like) || like.ValueKind != JsonValueKind.String) continue;
                var atlas = p.Value.TryGetProperty("atlas", out var a) && a.ValueKind == JsonValueKind.String
                    ? a.GetString() : p.Name.ToUpperInvariant();
                out_.Add(new Group { Name = p.Name, Like = like.GetString(), Atlas = atlas });
            }
            return out_;
        }

        public sealed class Plan
        {
            public Dictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.Ordinal);
            public string NewAtlasAddress, LikeAtlasAddress;
        }

        // the new group's keys from `like`'s ({key: address} as AssetBundlePathMatch holds them); null when `like`
        // has no atlas key (its name in capitals)
        public static Plan Keys(Group g, IDictionary<string, string> likeKeys)
        {
            var likeAtlasKey = g.Like.ToUpperInvariant();
            if (likeKeys == null || !likeKeys.TryGetValue(likeAtlasKey, out var likeAtlas) || string.IsNullOrEmpty(likeAtlas))
                return null;
            var plan = new Plan { LikeAtlasAddress = likeAtlas };
            int at = likeAtlas.LastIndexOf(likeAtlasKey, StringComparison.OrdinalIgnoreCase);
            var folder = likeAtlas.Substring(0, Math.Max(0, likeAtlas.LastIndexOf('/') + 1));
            var parent = folder.TrimEnd('/');
            parent = parent.Substring(0, Math.Max(0, parent.LastIndexOf('/') + 1));
            // ".../Chara/Map/MO_FF6_N001_C00/MO_FF6_N001_C00" -> ".../Chara/Map/<ATLAS>/<ATLAS>"
            plan.NewAtlasAddress = at >= 0 && parent.Length > 0 ? parent + g.Atlas + "/" + g.Atlas : likeAtlas + "#" + g.Atlas;
            foreach (var kv in likeKeys)
                if (!string.Equals(kv.Key, likeAtlasKey, StringComparison.OrdinalIgnoreCase)) plan.Keys[kv.Key] = kv.Value;
            plan.Keys[g.Atlas] = plan.NewAtlasAddress;
            plan.Keys[likeAtlasKey] = likeAtlas;      // loading the group loads like's atlas: the copy is made from it
            return plan;
        }
    }
}
