using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Last.Data.Master;
using Last.Management;

namespace PrKit
{
    // Patching the game without crashing it. Learned on FF6 (PORTING.md, "Plugin"); the engine is
    // the same in every PR, so it holds for all of them:
    //  1. The game's C++ is linked with identical-function folding: tiny methods (empty, one call)
    //     share one machine-code body with many unrelated methods. Hooking one hooks them all, the
    //     hook then reads some other object's fields, and that access violation is something .NET
    //     can't catch: the game closes (BNW v0.6.0 at startup). Patch() refuses methods whose code
    //     is that small, and any whose code another method has (SharedBy: folding isn't only tiny
    //     bodies). Hook the method that does the work, not a wrapper; a virtual one the game only
    //     calls through the vtable can take ReplaceVirtual instead.
    //  2. Every hook checks it got the type it patched before touching the object:
    //     if (!Hooks.IsA<StatusDetailsController>(__instance)) return;
    //  3. Il2CppClassPointerStore<T>.NativeClassPtr is 0 until the interop type's static
    //     constructor has run; ClassOf<T> runs it first (an IsA that rejected everything silently
    //     turned BNW's hooks off for several versions).
    //  4. A hook that throws logs once (WarnOnce) and leaves the game's own result.
    //  5. Harmony can't patch MasterManager's generic methods; ordinary game methods patch fine.
    //  6. Hooks run on several game threads (stat getters do): lock what they share.
    public static class Hooks
    {
        static readonly HashSet<string> warned = new HashSet<string>();
        static readonly HashSet<string> noted = new HashSet<string>();

        // Patches type.method with hooks.prefix / hooks.postfix (method names on the hooks type).
        // A missing method, shared code or a Harmony error is logged and leaves the game alone.
        public static bool Patch(Harmony h, Type type, string method, Type hooks, string prefix = null, string postfix = null,
            Type[] args = null)
        {
            MethodInfo original;
            try { original = AccessTools.Method(type, method, args); }
            catch (Exception e)
            {
                Kit.Warn($"[patch] {type?.Name}.{method}: {e.Message}");
                return false;
            }
            if (original == null)
            {
                Kit.Warn($"[patch] {type?.Name}.{method} not found");
                return false;
            }
            return Patch(h, original, hooks, prefix, postfix);
        }

        // The same for a method found some other way (an overload picked by its parameters). priority: Harmony's
        // (Priority.First .. Priority.Last), for hooks whose order against other mods' on the same method matters.
        public static bool Patch(Harmony h, MethodBase original, Type hooks, string prefix = null, string postfix = null,
            int priority = -1)
        {
            var where = $"{original?.DeclaringType?.Name}.{original?.Name}";
            try
            {
                if (original == null) { Kit.Warn("[patch] no method given"); return false; }
                var tiny = TinyNativeCode(original);
                if (tiny != null)
                {
                    Kit.Warn($"[patch] {where} not patched: its native code is {tiny}, which other methods share");
                    return false;
                }
                var sharedWith = SharedWith(original, out bool otherClasses);
                if (sharedWith != null && otherClasses)
                {
                    Kit.Warn($"[patch] {where} not patched: its native code is also {sharedWith} (identical-code folding), " +
                             "so the hook would run for those and get their objects. Hook its caller, or, for a virtual " +
                             "method the game only calls through the vtable, use Hooks.ReplaceVirtual");
                    return false;
                }
                // the same class's own twin (SpriteAtlas.GetSprites(Sprite[]) is GetSpritesScripting(Sprite[])'s code)
                if (sharedWith != null)
                    Kit.Info($"[patch] {where}: its native code is also {sharedWith} (same class): the hook runs for it too");
                var shortCode = ShortNativeCode(original);
                if (shortCode != null)
                {
                    // a postfix-only hook runs the game's code on every call: it would crash on the first one
                    if (prefix == null)
                    {
                        Kit.Warn($"[patch] {where} not patched: {shortCode}, which a detour breaks (the game's own code " +
                                 "would run into the padding: BNW 2026-11-21, GetBattleModeByConfigValue)");
                        return false;
                    }
                    Kit.Warn($"[patch] {where}: {shortCode}, which a detour breaks: patched, but the game's own code " +
                             "must never run (a prefix that always skips it)");
                }
                h.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(hooks, prefix) { priority = priority },
                    postfix: postfix == null ? null : new HarmonyMethod(hooks, postfix) { priority = priority });
                return true;
            }
            catch (Exception e)
            {
                Kit.Warn($"[patch] {where}: {e.Message}");
                return false;
            }
        }

        // A game type by its full name ("Serial.FF5.Management.JobData"), null (logged once) if this
        // game has none: for code that should run in several PRs without referencing a type one lacks.
        // Each loaded assembly is asked for the name (a table lookup). Not AccessTools.TypeByName: it
        // tries only the caller's assembly by name and then loads every type of every assembly, the
        // game's interop included (tens of thousands of types). That first scan took about 20 s of
        // Battle Options' start on the Steam Deck (2026-11-18), 0.45 s on a desktop PC.
        public static Type TypeByName(string fullName)
        {
            Type t = null;
            try
            {
                t = typeof(MasterManager).Assembly.GetType(fullName, false);   // the game's own code (Assembly-CSharp)
                if (t == null)
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                        if ((t = a.GetType(fullName, false)) != null) break;
            }
            catch (Exception e) { Kit.Warn($"[kit] type {fullName}: {e.Message}"); }
            if (t == null) NoteOnce("type-" + fullName, () => $"[kit] {Game.Describe()} has no {fullName}");
            return t;
        }

        // Empty or forwarding methods compile to a few bytes the linker shares between many methods.
        // Returns a description if the method's machine code is like that (just "ret", or a jump
        // first, or a ret within 6 bytes), else null.
        public static unsafe string TinyNativeCode(MethodBase method)
        {
            try
            {
                var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                var info = field == null ? IntPtr.Zero : (IntPtr)field.GetValue(null);
                if (info == IntPtr.Zero) return null;
                var code = *(byte**)info; // Il2CppMethodInfo.methodPointer is the first field
                if (code == null) return null;
                var hex = $"{code[0]:X2} {code[1]:X2} {code[2]:X2} {code[3]:X2} {code[4]:X2} {code[5]:X2} {code[6]:X2} {code[7]:X2}";
                if (code[0] == 0xC3) return $"empty (ret) [{hex}]";
                if (code[0] == 0xE9 || code[0] == 0xEB) return $"a jump [{hex}]";
                if (code[0] == 0x48 && code[1] == 0xFF && code[2] == 0x25) return $"a jump [{hex}]"; // jmp [rip+x]
                for (int i = 1; i < 6; i++)
                    if (code[i] == 0xC3) return $"{i + 1} bytes [{hex}]";
                return null;
            }
            catch (Exception e)
            {
                Kit.Warn($"[patch] can't inspect {method.Name}: {e.Message}");
                return null;
            }
        }

        // A short method (its ret, then int3 padding, within 24 bytes) with a short jump that lands inside it: the detour
        // copies the method's first instructions elsewhere, and that jump, moved with them, lands in the padding. Seen
        // 2026-11-21 (BNW's battles closed as they started): BattleUtility.GetBattleModeByConfigValue(int) is
        // xor eax,eax / test ecx,ecx / je +0A / mov edx,1 / cmp ecx,edx / cmove eax,edx / ret (17 bytes): an int3 at
        // +0x14. A short method without such a jump is fine (ColosseumInfomationData.IsRewardNameUnknown: cmp, sete, ret).
        // Byte patterns, not a disassembler: a false alarm costs a hook, a miss costs a crash.
        public static unsafe string ShortNativeCode(MethodBase method)
        {
            try
            {
                var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                var info = field == null ? IntPtr.Zero : (IntPtr)field.GetValue(null);
                if (info == IntPtr.Zero) return null;
                var code = *(byte**)info;
                if (code == null) return null;
                int end = -1;
                for (int i = 1; i < 24; i++)
                    if (code[i] == 0xC3 && code[i + 1] == 0xCC) { end = i; break; }
                if (end < 0) return null;
                for (int i = 0; i + 1 < end; i++)
                {
                    byte op = code[i];
                    if ((op >= 0x70 && op <= 0x7F) || op == 0xEB)   // jcc rel8, jmp rel8
                    {
                        int to = i + 2 + (sbyte)code[i + 1];
                        if (to > 0 && to <= end) return $"its native code is {end + 1} bytes with a short jump at byte {i} to byte {to}";
                    }
                }
                return null;
            }
            catch (Exception) { return null; }
        }

        // a generated method's Il2CppMethodInfo, and its machine code (Il2CppMethodInfo.methodPointer, the first field)
        static IntPtr MethodInfoPtr(MethodBase method)
        {
            var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            return field == null ? IntPtr.Zero : (IntPtr)field.GetValue(null);
        }

        static unsafe IntPtr NativeCode(MethodBase method)
        {
            var info = MethodInfoPtr(method);
            return info == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)info;
        }

        // How many of the game's methods run this method's machine code: 1 = its own, more = the linker folded identical
        // bodies into one (identical-code folding), 0 = unknown (not a game method, or the count failed). Not only tiny
        // methods: in FF6's GameAssembly (tools/il2cpp_out/dump.cs RVAs and the dll's bytes, 2026-11-21) a 4-byte setter
        // body (mov [rcx+70h],edx / ret) is 17 methods', MainMenuController.OnCommandMenuKeyAction's among them; a 7-byte
        // compare is CharacterInfomationData.IsChangeEquipmentOnlyAccessory's and three NoARBattle lambdas' (missed by the
        // 6-byte check above); 3,753 bodies over 32 bytes belong to more than one method. Size can't tell: the unique
        // ColosseumInfomationData.IsRewardNameUnknown is 7 bytes too. Every method of every class the game has is counted
        // once, on the first call, from il2cpp's own lists (assemblies, their classes, each class's methods: the walk
        // frida-il2cpp-bridge makes); the log says how long it took.
        public static int SharedBy(MethodBase method)
        {
            try
            {
                var code = NativeCode(method);
                if (code == IntPtr.Zero || !Bodies()) return 0;
                return sharers.TryGetValue(code, out var list) ? list.Count : firsts.ContainsKey(code) ? 1 : 0;
            }
            catch (Exception e) { WarnOnce("SharedBy", e); return 0; }
        }

        // The other methods with this method's machine code ("Class.Method, Class.Method (+n more)"), null if none (or
        // unknown); otherClasses: one of them is another class's (a hook would get that class's objects).
        public static string SharedWith(MethodBase method, out bool otherClasses)
        {
            otherClasses = false;
            try
            {
                var info = MethodInfoPtr(method);
                var code = info == IntPtr.Zero ? IntPtr.Zero : NativeCode(method);
                if (code == IntPtr.Zero || !Bodies() || !sharers.TryGetValue(code, out var list)) return null;
                var own = IntPtr.Zero;
                foreach (var (c, m) in list) if (m == info) own = c;
                var names = new List<string>();
                foreach (var (c, m) in list)
                {
                    if (m == info) continue;
                    if (c != own) otherClasses = true;
                    if (names.Count < 4) names.Add($"{Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(c))}.{Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(m))}");
                }
                if (own == IntPtr.Zero) otherClasses = true;   // the method itself wasn't listed: assume the worst
                int more = list.Count - 1 - names.Count;
                return names.Count == 0 ? null : string.Join(", ", names) + (more > 0 ? $" (+{more} more)" : "");
            }
            catch (Exception e) { WarnOnce("SharedWith", e); return null; }
        }

        public static string BodiesState { get; private set; } = "not counted yet";
        static Dictionary<IntPtr, (IntPtr Class, IntPtr Method)> firsts;                // body -> the first method seen with it
        static Dictionary<IntPtr, List<(IntPtr Class, IntPtr Method)>> sharers;         // body -> all its methods, if more than one
        static bool bodiesFailed;
        static readonly object bodiesLock = new object();

        // const Il2CppClass* il2cpp_image_get_class(const Il2CppImage*, size_t index): not in Il2CppInterop's IL2CPP
        [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr il2cpp_image_get_class(IntPtr image, UIntPtr index);

        static unsafe bool Bodies()
        {
            lock (bodiesLock)
            {
                if (sharers != null || bodiesFailed) return sharers != null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var first = new Dictionary<IntPtr, (IntPtr, IntPtr)>(1 << 17);
                    var shared = new Dictionary<IntPtr, List<(IntPtr, IntPtr)>>();
                    uint assemblies = 0;
                    int classes = 0, methods = 0;
                    var asms = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref assemblies);
                    for (uint a = 0; a < assemblies; a++)
                    {
                        var image = IL2CPP.il2cpp_assembly_get_image(asms[a]);
                        if (image == IntPtr.Zero) continue;
                        uint n = IL2CPP.il2cpp_image_get_class_count(image);
                        for (uint k = 0; k < n; k++)
                        {
                            var klass = il2cpp_image_get_class(image, (UIntPtr)k);
                            if (klass == IntPtr.Zero) continue;
                            classes++;
                            IntPtr iter = IntPtr.Zero, m;
                            while ((m = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                            {
                                var code = *(IntPtr*)m;   // abstract methods and open generics have none
                                if (code == IntPtr.Zero) continue;
                                methods++;
                                if (!first.TryGetValue(code, out var f)) { first[code] = (klass, m); continue; }
                                if (!shared.TryGetValue(code, out var list)) shared[code] = list = new List<(IntPtr, IntPtr)> { f };
                                list.Add((klass, m));
                            }
                        }
                    }
                    firsts = first;
                    sharers = shared;
                    BodiesState = $"{methods} methods of {classes} classes in {assemblies} assemblies: {first.Count} bodies, " +
                                  $"{shared.Count} of them shared ({sw.ElapsedMilliseconds} ms)";
                    Kit.Info("[patch] the game's native code counted: " + BodiesState);
                }
                catch (Exception e)
                {
                    bodiesFailed = true;
                    BodiesState = "count failed: " + e.Message;
                    Kit.Warn($"[patch] can't count the game's native code ({e.Message}): only the tiny-code check guards hooks");
                }
                return sharers != null;
            }
        }

        // Points class T's vtable entry for a virtual method at the plugin's own function, leaving the game's code as it
        // is: for a method whose code other methods share (Patch refuses it) that the game calls only through the vtable.
        // Check its callers first (tools/ghidra/callers.py): a direct call still runs the game's code. T's own vtable
        // only (a subclass has its own copy of the entry). fn: a delegate of a non-generic type with the native
        // signature, `this` first and the MethodInfo* last, bool returned as byte; kept alive here. True once the entry is
        // the plugin's (a second call for the same method and class: true at once); false with why in `what` (a class the
        // game hasn't set up yet has no entry: try again later). gameCode: the game's own code, for a self-test.
        //   delegate byte OnlyAccessoryFn(IntPtr self, int characterStatusId, IntPtr method);
        //   Hooks.ReplaceVirtual<CharacterInfomationData>(m, (OnlyAccessoryFn)OnlyAccessory, out var what, out _);
        // Worlds Collide's WcRecruits and BNW's MenuRules did this by hand (Umaro's equip menu, 2026-11-21).
        static readonly Dictionary<(IntPtr Class, IntPtr Method), (Delegate Fn, string What)> replaced =
            new Dictionary<(IntPtr, IntPtr), (Delegate, string)>();

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        public static unsafe bool ReplaceVirtual<T>(MethodBase method, Delegate fn, out string what, out IntPtr gameCode)
            where T : Il2CppObjectBase
        {
            gameCode = IntPtr.Zero;
            try
            {
                var info = method == null ? IntPtr.Zero : MethodInfoPtr(method);
                var klass = ClassOf<T>.Ptr;
                if (info == IntPtr.Zero || klass == IntPtr.Zero || fn == null)
                {
                    what = $"not replaced: no {(info == IntPtr.Zero ? "method info" : klass == IntPtr.Zero ? "game class" : "function")}";
                    return false;
                }
                var code = *(IntPtr*)info;
                gameCode = code;
                lock (replaced)
                {
                    if (replaced.TryGetValue((klass, info), out var done)) { what = done.What; return true; }
                    var dll = GetModuleHandle("GameAssembly.dll");
                    var at = dll == IntPtr.Zero ? "unknown" : $"RVA 0x{(long)code - (long)dll:X}";
                    var cls = Il2CppInterop.Runtime.Runtime.UnityVersionHandler.Wrap((Il2CppInterop.Runtime.Runtime.Il2CppClass*)klass);
                    var vt = (IntPtr*)cls.VTable;
                    int slots = cls.VtableCount;
                    for (int i = 0; vt != null && i < slots; i++)
                    {
                        var e = vt + 2 * i;   // VirtualInvokeData { methodPtr, method }: both must be the method's own
                        if (e[0] != code || e[1] != info) continue;
                        e[0] = Marshal.GetFunctionPointerForDelegate(fn);
                        what = $"vtable slot {i} of {typeof(T).Name} is the plugin's (the game's code at {at} left as it is)";
                        replaced[(klass, info)] = (fn, what);
                        return true;
                    }
                    what = $"not replaced: no vtable slot of {typeof(T).Name} ({slots} slots) runs {method.Name}'s code at {at} " +
                           "(a class the game hasn't set up yet: try again later)";
                    return false;
                }
            }
            catch (Exception e) { what = "not replaced: " + e.Message; return false; }
        }

        // Is o really a T (or derived)? Every hook asks before touching its object (rule 2 above).
        public static bool IsA<T>(Il2CppObjectBase o) where T : Il2CppObjectBase
        {
            if (o == null || o.Pointer == IntPtr.Zero) return false;
            var want = ClassOf<T>.Ptr;
            if (want == IntPtr.Zero) return false;
            var klass = IL2CPP.il2cpp_object_get_class(o.Pointer);
            if (klass == want || IL2CPP.il2cpp_class_is_assignable_from(want, klass)) return true; // as TryCast<T> does
            NoteOnce("guard-" + typeof(T).Name, () =>
                $"[guard] {typeof(T).Name} hook got a {Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass))} (left alone)");
            return false;
        }

        public static IntPtr ClassPtr<T>() where T : Il2CppObjectBase => ClassOf<T>.Ptr;

        // The game class pointer for an interop type, resolved once (rule 3 above).
        static class ClassOf<T> where T : Il2CppObjectBase
        {
            internal static readonly IntPtr Ptr = Resolve();

            static IntPtr Resolve()
            {
                var p = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (p == IntPtr.Zero)
                {
                    System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
                    p = Il2CppClassPointerStore<T>.NativeClassPtr;
                }
                if (p == IntPtr.Zero) Kit.Warn($"[guard] no game class for {typeof(T).Name}; its hooks are off");
                return p;
            }
        }

        // For the log at startup: the guard accepts the right type and rejects another.
        public static string GuardSelfTest()
        {
            var mm = MasterManager.Instance;
            if (mm == null) return "[guard] self-test: no MasterManager yet";
            return $"[guard] self-test: MasterManager accepted={IsA<MasterManager>(mm)}, as UserDataManager rejected={!IsA<UserDataManager>(mm)}";
        }

        // One warning per place: hooks run every frame and on several threads.
        public static void WarnOnce(string where, Exception e)
        {
            bool first;
            lock (warned) first = warned.Add(where);
            if (first) Kit.Warn($"[{where}] {e.GetType().Name}: {e.Message}");
        }

        public static void NoteOnce(string key, Func<string> text)
        {
            bool first;
            lock (noted) first = noted.Add(key);
            if (first) Kit.Info(text());
        }
    }
}
