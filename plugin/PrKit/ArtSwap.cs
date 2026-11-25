using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Last.Management;
using UnityEngine;
using UnityEngine.U2D;

namespace PrKit
{
    // Art served from a folder instead of the game's bundles, swapped live (no restart, no Magicite
    // files): a randomizer seed's character sprites, portraits and monster images (Beyond Chaos'
    // makeover and remonsterate, 2026-11-20). Magicite can't do that: it only maps files present at
    // its start, and keeps the sprites of the atlases it builds for good.
    //
    //   ArtSwap.Install(harmony);            once, in the plugin's Load
    //   ArtSwap.Load(dir);                   dir/art.json (null: none) is now the served art (the base layer)
    //   ArtSwap.SetLayer("costume:terra", dir)   one more set on top (null: off again); later layers win where
    //                                        they serve the same address or atlas frame (a costume per character
    //                                        over a seed's art, each switched on its own). Safe mid-scene:
    //                                        a named layer's old textures stay loaded (what's on screen keeps
    //                                        drawing; reused if it comes back); only a base Load frees them
    //
    // art.json: {"sprites": {address: {"png": file, "rect": [x, y, w, h], "pivot": [px, py], "ppu": 1, "border": [l, b, r, t]}},
    //            "atlases": {atlas name: {sprite name: {png, rect, pivot}}}}
    // rect is top-down pixels in the PNG (as tools/snes_charsprites.py pack writes it); pivot is
    // Unity's (0..1, from the bottom left); 1 pixel per unit, point filtered (the PR's own setting).
    // - sprites: the game asks ResourceManager for an address ("Assets/GameAssets/Serial/Res/Chara/
    //   Battle/BC_FF6_P001/Idle_00"); CheckCompleteAsset's postfix (as Magicite's) puts ours in
    //   completeAssetDic. Entries the game already holds are replaced on Load (the originals kept and
    //   put back when a later Load no longer serves them).
    // - atlases: a field character's frames are one SpriteAtlas ("MO_FF6_P001"); GetSprite, GetSprites
    //   and spriteCount answer from ours (as Magicite does for its own .atlas files). Characters already
    //   on the map keep their frames until the map reloads.
    // - keys: {group: {key: address}} the game doesn't have (a monster image of its own: a master row
    //   points at group + key); added to AssetBundlePathMatch (as Magicite adds its new keys), and the
    //   address is in completeAssetDic from Load on, so the game never asks its bundles for it.
    // - new_groups: {group: {"like": an existing NPC group, "atlas": name}}: a field NPC group the game never shipped,
    //   its atlas a renamed copy of `like`'s (ArtNewGroups.cs); the mod's frames come from `atlases` under its name.
    public static class ArtSwap
    {
        public static string Tag = "[art]";

        sealed class Entry
        {
            public string Folder;      // the layer's folder (Png is relative to it)
            public string Png;
            public Rect Rect;          // Unity's (bottom-up)
            public Vector2 Pivot;
            public float Ppu = 1f;     // pixels per unit (the PR's characters and monsters: 1; its UI: 100)
            public Vector4 Border;     // a 9-slice UI sprite's borders (left, bottom, right, top)
            public Sprite Sprite;      // made on first use
            public bool New;           // an address of a key we added: the game has no asset there
            public bool Preload;       // put in completeAssetDic on Load (else when the game first checks it)
        }

        static readonly object gate = new object();
        static Dictionary<string, Entry> sprites = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, Dictionary<string, Entry>> atlases = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Il2CppSystem.Object> originals = new Dictionary<string, Il2CppSystem.Object>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Sprite> uiOriginals = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        const string UiAddress = "Assets/GameAssets/Common/UI/Common/Sprites/";
        static readonly HashSet<IntPtr> ours = new HashSet<IntPtr>();
        // every sprite made here (served or shadowed by a later layer): never mistaken for the game's original
        static readonly HashSet<IntPtr> made = new HashSet<IntPtr>();
        sealed class Layer
        {
            public string Name, Folder;
            public Dictionary<string, Entry> Sprites;
            public Dictionary<string, Dictionary<string, Entry>> Atlases;
        }
        static readonly List<Layer> layers = new List<Layer>();
        static bool installed;
        public static int Served, AtlasServed;
        public static string Last = "none";

        public static void Install(Harmony h)
        {
            if (installed) return;
            installed = true;
            Hooks.Patch(h, typeof(ResourceManager), "CheckCompleteAsset", typeof(ArtSwap), postfix: nameof(CheckCompletePost));
            Hooks.Patch(h, typeof(SpriteAtlas), "GetSprite", typeof(ArtSwap), prefix: nameof(GetSpritePre), args: new[] { typeof(string) });
            Hooks.Patch(h, typeof(SpriteAtlas), "GetSprites", typeof(ArtSwap), prefix: nameof(GetSpritesPre),
                args: new[] { typeof(Il2CppReferenceArray<Sprite>) });
            Hooks.Patch(h, AccessTools.PropertyGetter(typeof(SpriteAtlas), "spriteCount"), typeof(ArtSwap), postfix: nameof(SpriteCountPost));
            Hooks.Patch(h, typeof(UnityEngine.UI.Image), "OnEnable", typeof(ArtSwap), postfix: nameof(ImageEnablePost));
        }

        // Serve folder/art.json as the base layer from now on (null or no file: none). Returns a line for the log.
        public static string Load(string folder) => SetLayer("", folder);

        // A named set of art over the base and the layers set before it (null or no art.json: that layer off).
        public static string SetLayer(string name, string folder)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var layer = new Layer
            {
                Name = name ?? "", Folder = folder,
                Sprites = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase),
                Atlases = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.OrdinalIgnoreCase),
            };
            var path = folder == null ? null : Path.Combine(folder, "art.json");
            bool on = path != null && File.Exists(path);
            if (on)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("sprites", out var s))
                        foreach (var p in s.EnumerateObject()) layer.Sprites[p.Name] = Read(p.Value, folder);
                    if (root.TryGetProperty("keys", out var k))
                        foreach (var g in k.EnumerateObject())
                            foreach (var p in g.Value.EnumerateObject())
                            {
                                var addr = p.Value.GetString();
                                AddKey(g.Name, p.Name, addr);
                                if (layer.Sprites.TryGetValue(addr, out var en)) en.New = true;
                            }
                    foreach (var g in ArtNewGroups.Parse(root)) AddGroup(g);
                    if (root.TryGetProperty("atlases", out var a))
                        foreach (var at in a.EnumerateObject())
                        {
                            var m = new Dictionary<string, Entry>(StringComparer.Ordinal);
                            foreach (var p in at.Value.EnumerateObject()) m[p.Name] = Read(p.Value, folder);
                            layer.Atlases[at.Name] = m;
                        }
                }
                catch (Exception e)
                {
                    Kit.Warn($"{Tag} {path}: {e.Message}");
                    on = false;
                }
            }
            lock (gate)
            {
                int index = layers.FindIndex(l => l.Name == layer.Name);
                if (index >= 0) layers.RemoveAt(index);
                if (on) layers.Insert(index >= 0 ? index : layers.Count, layer);
                // what all layers serve now, later layers over earlier ones
                var old = sprites;
                var mergedSprites = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                var mergedAtlases = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in layers)
                {
                    foreach (var kv in l.Sprites) mergedSprites[kv.Key] = kv.Value;
                    foreach (var kv in l.Atlases)
                    {
                        if (!mergedAtlases.TryGetValue(kv.Key, out var m))
                            mergedAtlases[kv.Key] = m = new Dictionary<string, Entry>(StringComparer.Ordinal);
                        foreach (var f in kv.Value) m[f.Key] = f.Value;
                    }
                }
                sprites = mergedSprites;
                atlases = mergedAtlases;
                // textures no served entry uses any more go only when the base changes (a seed applied: no scene
                // showing them); a named layer switched mid-scene (a costume) keeps them, since what's on screen may
                // still draw the old sprite, and they're reused if that layer comes back.
                // ours: the sprites made so far that are still served
                if (layer.Name == "")
                {
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in AllEntries()) used.Add(Path.Combine(e.Folder ?? "", e.Png));
                    foreach (var key in new List<string>(textures.Keys))
                        if (!used.Contains(key))
                        {
                            if (textures[key] != null) UnityEngine.Object.Destroy(textures[key]);
                            textures.Remove(key);
                        }
                }
                ours.Clear();
                foreach (var e in AllEntries())
                    if (e.Sprite != null && !e.Sprite.WasCollected && e.Sprite) ours.Add(e.Sprite.Pointer);
                Served = AtlasServed = 0;
                int replaced = 0, restored = 0;
                var dic = ResourceManager.Instance?.completeAssetDic;
                if (dic != null)
                {
                    // addresses no longer ours: the game's own asset back (or gone, to be loaded again)
                    foreach (var addr in old.Keys)
                    {
                        if (sprites.ContainsKey(addr) || !dic.ContainsKey(addr)) continue;
                        if (originals.TryGetValue(addr, out var orig) && orig != null) dic[addr] = orig;
                        else dic.Remove(addr);
                        originals.Remove(addr);
                        restored++;
                    }
                    // addresses the game already holds, and preloaded entries the game
                    // may fetch from another cache without calling CheckCompleteAsset
                    foreach (var kv in sprites)
                    {
                        if (!dic.ContainsKey(kv.Key))
                        {
                            if (!kv.Value.Preload) continue;
                            var made = Make(kv.Key, kv.Value);
                            if (made != null) { dic[kv.Key] = made; replaced++; }
                            continue;
                        }
                        if (!dic.ContainsKey(kv.Key)) continue;
                        var spr = Make(kv.Key, kv.Value);
                        if (spr == null) continue;
                        var cur = dic[kv.Key];
                        if (cur != null && cur.Pointer == spr.Pointer) continue;              // already this one
                        bool curOurs = cur != null && (ours.Contains(cur.Pointer) || made.Contains(cur.Pointer));
                        if (!originals.ContainsKey(kv.Key) && !curOurs) originals[kv.Key] = cur;
                        dic[kv.Key] = spr;
                        replaced++;
                    }
                }
                var names = string.Join(", ", layers.ConvertAll(l => l.Name == "" ? "base" : l.Name));
                Last = $"{(layer.Name == "" ? "base" : layer.Name)}: {(on ? folder : "off")}; layers [{names}]: {sprites.Count} sprites, " +
                       $"{atlases.Count} atlases; {replaced} loaded assets replaced, {restored} restored; {clock.ElapsedMilliseconds} ms";
            }
            RefreshUiImages();
            Kit.Info($"{Tag} {Last}");
            return Last;
        }

        // UI prefabs can keep a Sprite reference from before a seed is applied. The
        // ResourceManager dictionary alone cannot change those Image components.
        static void ImageEnablePost(UnityEngine.UI.Image __instance) => RefreshUiImage(__instance);

        static void RefreshUiImages()
        {
            try
            {
                foreach (var image in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Image>())
                    RefreshUiImage(image);
            }
            catch (Exception ex) { Hooks.WarnOnce("art UI refresh", ex); }
        }

        static void RefreshUiImage(UnityEngine.UI.Image image)
        {
            try
            {
                var current = image?.sprite;
                var name = current?.name;
                if (string.IsNullOrEmpty(name) || !name.StartsWith("UI_Common_WindowFrame", StringComparison.OrdinalIgnoreCase)) return;
                lock (gate)
                {
                    var address = UiAddress + name;
                    if (sprites.TryGetValue(address, out var entry))
                    {
                        var replacement = Make(address, entry);
                        if (replacement == null || current.Pointer == replacement.Pointer) return;
                        if (!made.Contains(current.Pointer) && !uiOriginals.ContainsKey(name)) uiOriginals[name] = current;
                        image.sprite = replacement;
                    }
                    else if (made.Contains(current.Pointer) && uiOriginals.TryGetValue(name, out var original) && original != null)
                        image.sprite = original;
                }
            }
            catch (Exception ex) { Hooks.WarnOnce("art UI image", ex); }
        }

        static IEnumerable<Entry> AllEntries()
        {
            foreach (var e in sprites.Values) yield return e;
            foreach (var m in atlases.Values) foreach (var e in m.Values) yield return e;
        }

        // a key the game's AssetBundlePathMatch doesn't have (group + key -> address), as Magicite adds its own
        static readonly HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly List<(string Group, string Key, string Address)> pending = new List<(string, string, string)>();
        static void AddKey(string group, string key, string address)
        {
            if (!added.Add(group + "/" + key)) return;
            try
            {
                var data = Il2CppSystem.Asset.AssetBundlePathMatch.Instance?.originalData;
                if (data == null)
                {
                    // the game hasn't read its path list yet (a seed applied very early): again on the next check
                    added.Remove(group + "/" + key);
                    lock (gate) pending.Add((group, key, address));
                    return;
                }
                if (!data.ContainsKey(group)) data.Add(group, new Il2CppSystem.Collections.Generic.Dictionary<string, string>());
                var g = data[group];
                if (g.ContainsKey(key)) g[key] = address;
                else g.Add(key, address);
            }
            catch (Exception e) { added.Remove(group + "/" + key); Hooks.WarnOnce("art keys", e); }
        }

        // a new group (ArtNewGroups): its keys from `like`'s, its atlas address answered in CheckCompletePost
        static readonly Dictionary<string, (string Like, string Atlas, string LikeGroup, string LikeKey)> newAtlases =
            new Dictionary<string, (string, string, string, string)>(StringComparer.OrdinalIgnoreCase);
        // a new group's atlas waiting for like's: since when (asked for like's after ~5 s, a warning after ~15 s)
        static readonly Dictionary<string, DateTime> waitingSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> askedFor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> warnedFor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly List<ArtNewGroups.Group> pendingGroups = new List<ArtNewGroups.Group>();
        static readonly HashSet<string> madeGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static void AddGroup(ArtNewGroups.Group g)
        {
            try
            {
                var data = Il2CppSystem.Asset.AssetBundlePathMatch.Instance?.originalData;
                if (data == null) { lock (gate) pendingGroups.Add(g); return; }   // the path list isn't read yet
                if (!data.ContainsKey(g.Like)) { Kit.Warn($"{Tag} new group {g.Name}: no group {g.Like} to copy"); return; }
                var likeKeys = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in data[g.Like]) likeKeys[kv.Key] = kv.Value;
                var plan = ArtNewGroups.Keys(g, likeKeys);
                if (plan == null) { Kit.Warn($"{Tag} new group {g.Name}: {g.Like} has no atlas key"); return; }
                foreach (var kv in plan.Keys) AddKey(g.Name, kv.Key, kv.Value);
                lock (gate) newAtlases[plan.NewAtlasAddress] = (plan.LikeAtlasAddress, g.Atlas, g.Like, g.Like.ToUpperInvariant());
            }
            catch (Exception e) { Hooks.WarnOnce("art new group " + g.Name, e); }
        }

        // the game checks a new group's atlas address: a copy of `like`'s atlas named as the group's, once `like`'s is
        // loaded (the group's keys include `like`'s atlas, so loading the group loads it); false until then
        static bool NewAtlas(string addressName, ResourceManager rm, ref bool result)
        {
            (string Like, string Atlas, string LikeGroup, string LikeKey) na;
            lock (gate) if (!newAtlases.TryGetValue(addressName, out na)) return false;
            var dic = rm.completeAssetDic;
            if (dic == null) return true;
            if (dic.ContainsKey(addressName) && dic[addressName] != null) { result = true; return true; }
            if (!dic.ContainsKey(na.Like) || dic[na.Like] == null)
            {
                // like's atlas not loaded yet: after ~5 s ask for it ourselves (as ArtHarness's `atlas` does), after
                // ~15 s say so once (the group's NPC would otherwise wait unseen)
                var now = DateTime.UtcNow;
                if (!waitingSince.TryGetValue(addressName, out var since)) waitingSince[addressName] = since = now;
                var waited = (now - since).TotalSeconds;
                if (waited > 5 && askedFor.Add(addressName))
                {
                    try { rm.RequestLoadAssetBundle<SpriteAtlas>(na.LikeGroup, na.LikeKey, false); }
                    catch (Exception e) { Hooks.WarnOnce("art new group load " + na.Atlas, e); }
                    Kit.Info($"{Tag} new group atlas {na.Atlas}: asked for {na.LikeGroup}/{na.LikeKey} after {waited:0.0} s");
                }
                if (waited > 15 && warnedFor.Add(addressName))
                    Kit.Warn($"{Tag} new group {na.Atlas}: like's atlas {na.Like} never loaded ({waited:0} s)");
                result = false;
                return true;
            }
            var src = dic[na.Like].TryCast<SpriteAtlas>();
            if (src == null) { Hooks.WarnOnce("art new group " + na.Atlas, new InvalidCastException(na.Like + " isn't a SpriteAtlas")); return true; }
            var copy = UnityEngine.Object.Instantiate(src);
            copy.name = na.Atlas;
            copy.hideFlags = HideFlags.DontUnloadUnusedAsset;
            dic[addressName] = copy;
            result = true;
            if (madeGroups.Add(addressName))
                Kit.Info($"{Tag} new group atlas {na.Atlas}: a copy of {na.Like}" +
                         (waitingSince.TryGetValue(addressName, out var t0) ? $" (after {(DateTime.UtcNow - t0).TotalSeconds:0.0} s)" : ""));
            return true;
        }

        public static bool Serves(string address) { lock (gate) return sprites.ContainsKey(address); }
        public static int Count { get { lock (gate) return sprites.Count + atlases.Count; } }

        static Entry Read(JsonElement e, string folder)
        {
            var r = e.GetProperty("rect");
            float x = r[0].GetSingle(), y = r[1].GetSingle(), w = r[2].GetSingle(), h = r[3].GetSingle();
            var pv = e.TryGetProperty("pivot", out var p) ? new Vector2(p[0].GetSingle(), p[1].GetSingle()) : new Vector2(0.5f, 0f);
            var en = new Entry { Folder = folder, Png = e.GetProperty("png").GetString(), Rect = new Rect(x, y, w, h), Pivot = pv };
            if (e.TryGetProperty("ppu", out var u)) en.Ppu = u.GetSingle();
            if (e.TryGetProperty("preload", out var pl) && pl.ValueKind == JsonValueKind.True) en.Preload = true;
            if (e.TryGetProperty("border", out var b)) en.Border = new Vector4(b[0].GetSingle(), b[1].GetSingle(), b[2].GetSingle(), b[3].GetSingle());
            return en;
        }

        // the entry's Sprite (its PNG loaded once); null if the file can't be read
        static Sprite Make(string name, Entry e)
        {
            if (e.Sprite != null && !e.Sprite.WasCollected && e.Sprite) return e.Sprite;   // (Unity's alive check: the game may destroy assets it unloads)
            try
            {
                var file = Path.Combine(e.Folder ?? "", e.Png);
                if (!textures.TryGetValue(file, out var tex) || tex == null)
                {
                    var bytes = File.ReadAllBytes(file);
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = e.Png };
                    if (!ImageConversion.LoadImage(tex, bytes)) throw new InvalidDataException("not a PNG");
                    tex.filterMode = FilterMode.Point;
                    tex.hideFlags = HideFlags.HideAndDontSave;
                    textures[file] = tex;
                }
                // top-down rect in the file -> Unity's bottom-up
                var r = new Rect(e.Rect.x, tex.height - e.Rect.y - e.Rect.height, e.Rect.width, e.Rect.height);
                var spr = Sprite.Create(tex, r, e.Pivot, e.Ppu, 0, SpriteMeshType.FullRect, e.Border);
                spr.name = Path.GetFileName(name);
                spr.hideFlags = HideFlags.HideAndDontSave;
                e.Sprite = spr;
                ours.Add(spr.Pointer);
                made.Add(spr.Pointer);
                return spr;
            }
            catch (Exception ex)
            {
                Hooks.WarnOnce("art " + e.Png, ex);
                return null;
            }
        }

        // after Magicite's own postfix on the same method (Hooks.Patch's HarmonyMethod reads this attribute):
        // where both could serve an address, ArtSwap's (the seed's) is the one the game gets
        [HarmonyPriority(Priority.Last)]
        static void CheckCompletePost(string addressName, ResourceManager __instance, ref bool __result)
        {
            try
            {
                if (addressName == null || !Hooks.IsA<ResourceManager>(__instance)) return;
                if (pending.Count > 0)
                {
                    List<(string, string, string)> again;
                    lock (gate) { again = new List<(string, string, string)>(pending); pending.Clear(); }
                    foreach (var (g, k, a) in again) AddKey(g, k, a);
                }
                if (pendingGroups.Count > 0)
                {
                    List<ArtNewGroups.Group> again;
                    lock (gate) { again = new List<ArtNewGroups.Group>(pendingGroups); pendingGroups.Clear(); }
                    foreach (var g in again) AddGroup(g);
                }
                if (newAtlases.Count > 0 && NewAtlas(addressName, __instance, ref __result)) return;
                lock (gate)
                {
                    if (!sprites.TryGetValue(addressName, out var e)) return;
                    var dic = __instance.completeAssetDic;
                    if (dic == null) return;
                    if (dic.ContainsKey(addressName))
                    {
                        var cur = dic[addressName];
                        if (cur != null && ours.Contains(cur.Pointer)) { __result = true; return; }
                        var spr = Make(addressName, e);
                        if (spr == null) return;
                        if (!originals.ContainsKey(addressName)) originals[addressName] = cur;
                        dic[addressName] = spr;
                    }
                    else
                    {
                        if (!__result && !e.New) return;   // still loading: ours goes in once the game has its own
                        var spr = Make(addressName, e);
                        if (spr == null) return;
                        dic[addressName] = spr;
                    }
                    __result = true;
                    Served++;
                }
            }
            catch (Exception ex) { Hooks.WarnOnce("art CheckCompleteAsset", ex); }
        }

        static Dictionary<string, Entry> AtlasOf(SpriteAtlas atlas)
        {
            if (atlas == null || atlas.Pointer == IntPtr.Zero || atlases.Count == 0) return null;
            var n = atlas.name;
            return n != null && atlases.TryGetValue(n, out var m) ? m : null;
        }

        static bool GetSpritePre(SpriteAtlas __instance, string name, ref Sprite __result)
        {
            try
            {
                lock (gate)
                {
                    var m = AtlasOf(__instance);
                    if (m == null || name == null) return true;
                    if (!m.TryGetValue(name, out var e)) return true;    // a frame we don't have: the game's own
                    var spr = Make(__instance.name + "/" + name, e);
                    if (spr == null) return true;
                    __result = UnityEngine.Object.Instantiate(spr);
                    AtlasServed++;
                    return false;
                }
            }
            catch (Exception ex) { Hooks.WarnOnce("art GetSprite", ex); return true; }
        }

        static bool GetSpritesPre(SpriteAtlas __instance, ref Il2CppReferenceArray<Sprite> sprites, ref int __result)
        {
            try
            {
                lock (gate)
                {
                    var m = AtlasOf(__instance);
                    if (m == null || sprites == null || sprites.Length != m.Count) return true;
                    int i = 0;
                    foreach (var kv in m)
                    {
                        var spr = Make(__instance.name + "/" + kv.Key, kv.Value);
                        if (spr == null) return true;
                        sprites[i++] = UnityEngine.Object.Instantiate(spr);
                    }
                    __result = i;
                    AtlasServed++;
                    return false;
                }
            }
            catch (Exception ex) { Hooks.WarnOnce("art GetSprites", ex); return true; }
        }

        static void SpriteCountPost(SpriteAtlas __instance, ref int __result)
        {
            try
            {
                lock (gate)
                {
                    var m = AtlasOf(__instance);
                    if (m != null) __result = m.Count;
                }
            }
            catch (Exception ex) { Hooks.WarnOnce("art spriteCount", ex); }
        }
    }
}
