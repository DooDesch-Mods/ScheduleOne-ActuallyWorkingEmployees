using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ActuallyWorkingEmployees.Compat
{
    /// <summary>
    /// The native code address behind an interop method, and Harmony target lists with one entry per address.
    ///
    /// The game's native build folds functions with identical machine code into one (for example every
    /// GetActionDuration that only returns 10f). Harmony sees one MethodInfo per C# method, so patching two of them
    /// detours the same address twice: the second detour captures the first as its "original", every call re-enters
    /// the detour, and the game dies of a stack overflow with nothing in the log. A patch on such methods must
    /// target each address once and tell the real class apart inside the patch.
    /// </summary>
    internal static class NativeMethods
    {
        private static readonly List<KeyValuePair<bool, string>> _pending = new List<KeyValuePair<bool, string>>();
        private static readonly string OwnAssembly = typeof(NativeMethods).Assembly.GetName().Name;

        /// <summary>
        /// Harmony owner text of Harder Working Employees. Its patches get info lines instead of warnings: the one
        /// [Compat] warning already tells the player to remove it.
        /// </summary>
        private const string HweOwner = "HarderWorkingEmployees";

        private static bool IsHwe(string owners) => owners != null && owners.IndexOf(HweOwner, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Address of the native function that <paramref name="m"/> calls, read from its NativeMethodInfoPtr_ field
        /// (Il2CppMethodInfo.methodPointer is the struct's first field). IntPtr.Zero when the field is not found.
        /// </summary>
        internal static IntPtr Address(MethodBase m)
        {
            IntPtr a = TryAddress(m, out string problem);
            if (problem != null) Note("[NativeMethods] " + problem);
            return a;
        }

        // Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod reads the NativeMethodInfoPtr_ field that the
        // generated method body loads, so it tells overloads apart. Looked up by reflection: Il2CppInterop.Common is not a
        // compile reference of this mod.
        private static Func<MethodBase, FieldInfo> _fieldOf;
        private static bool _fieldOfLooked;

        private static FieldInfo InteropField(MethodBase m)
        {
            if (!_fieldOfLooked)
            {
                _fieldOfLooked = true;
                try
                {
                    MethodInfo mi = Type.GetType("Il2CppInterop.Common.Il2CppInteropUtils, Il2CppInterop.Common")
                        ?.GetMethod("GetIl2CppMethodInfoPointerFieldForGeneratedMethod", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(MethodBase) }, null);
                    if (mi != null) _fieldOf = (Func<MethodBase, FieldInfo>)Delegate.CreateDelegate(typeof(Func<MethodBase, FieldInfo>), mi);
                    else Note("[NativeMethods] Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod not found; overloaded methods stay unresolved.", true);
                }
                catch (Exception e) { Note("[NativeMethods] Il2CppInteropUtils lookup failed, " + e.GetType().Name + ": " + e.Message + "; overloaded methods stay unresolved.", true); }
            }
            return _fieldOf?.Invoke(m);
        }

        /// <summary>Like <see cref="Address"/>, but returns the reason instead of logging it. <paramref name="problem"/> is null on success.</summary>
        private static IntPtr TryAddress(MethodBase m, out string problem)
        {
            problem = null;
            Type t = m?.DeclaringType;
            if (t == null) { problem = "method without a declaring type, address not resolved."; return IntPtr.Zero; }
            FieldInfo found = null;
            string interopProblem = null;
            try { found = InteropField(m); }
            catch (Exception e) { interopProblem = "Il2CppInterop field lookup failed, " + e.GetType().Name + ": " + e.Message; }
            if (found == null)
            {
                // Fallback by name: only unambiguous when the method has no overloads.
                string prefix = "NativeMethodInfoPtr_" + m.Name + "_";
                foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (!f.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (found != null)
                    {
                        problem = t.Name + "." + m.Name + " has several overloads, address not resolved" + (interopProblem != null ? " (" + interopProblem + ")" : "") + ".";
                        return IntPtr.Zero;
                    }
                    found = f;
                }
            }
            if (found == null) { problem = t.Name + "." + m.Name + " has no NativeMethodInfoPtr_ field, address not resolved."; return IntPtr.Zero; }
            IntPtr info = (IntPtr)found.GetValue(null);
            if (info == IntPtr.Zero) { problem = t.Name + "." + m.Name + ": NativeMethodInfoPtr_ is not set, address not resolved."; return IntPtr.Zero; }
            return Marshal.ReadIntPtr(info);
        }

        /// <summary>
        /// Every interop method that a Harmony owner other than this mod has patched so far, by native address, with a
        /// description "Type.Method (owner, owner)". Methods whose address cannot be read are left out.
        /// </summary>
        private static Dictionary<IntPtr, List<KeyValuePair<MethodBase, string>>> ForeignPatched(bool includeOwn = false)
        {
            var map = new Dictionary<IntPtr, List<KeyValuePair<MethodBase, string>>>();
            foreach (MethodBase m in HarmonyLib.Harmony.GetAllPatchedMethods())
            {
                try
                {
                    Type t = m.DeclaringType;
                    if (t == null || !typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(t)) continue;
                    HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    var others = new List<string>();
                    foreach (string owner in info.Owners)
                        if (owner != null && (includeOwn || owner.IndexOf(OwnAssembly, StringComparison.OrdinalIgnoreCase) < 0)) others.Add(owner);
                    if (others.Count == 0) continue;
                    IntPtr a = TryAddress(m, out _);
                    if (a == IntPtr.Zero) continue;
                    if (!map.TryGetValue(a, out var list)) map[a] = list = new List<KeyValuePair<MethodBase, string>>();
                    list.Add(new KeyValuePair<MethodBase, string>(m, t.Name + "." + m.Name + " (" + string.Join(", ", others) + ")"));
                }
                catch (Exception e) { Note("[NativeMethods] could not inspect a patched method of " + m?.DeclaringType?.Name + ": " + e.GetType().Name + ": " + e.Message, true); }
            }
            return map;
        }

        /// <summary>
        /// False when <paramref name="m"/> shares its native address with a different method that any mod (this one
        /// included) has already patched: a second detour on that address overflows the stack. Also false when the address
        /// cannot be read, because the method may share code with a patched one. Meant for a patch class's
        /// <c>Prepare(MethodBase)</c>. Logs one warning per refused method.
        /// </summary>
        internal static bool AllowPatch(MethodBase m)
        {
            string label = m?.DeclaringType?.Name + "." + m?.Name;
            IntPtr a;
            string problem;
            try { a = TryAddress(m, out problem); }
            catch (Exception e) { a = IntPtr.Zero; problem = "address read failed, " + e.GetType().Name + ": " + e.Message; }
            if (a == IntPtr.Zero)
            {
                Note("[NativeMethods] " + label + " not patched, its native address is unknown (" + problem + ").", true);
                return false;
            }
            if (!ForeignPatched(true).TryGetValue(a, out var list)) return true;
            foreach (var kv in list)
            {
                if (kv.Key == m) continue;
                Note("[NativeMethods] " + label + " not patched: its native code at 0x" + a.ToString("X")
                    + " is already patched through " + kv.Value + ".", !IsHwe(kv.Value));
                return false;
            }
            return true;
        }

        /// <summary>
        /// <paramref name="methods"/> reduced to one method per native address, in the given order. A method whose address
        /// cannot be read is refused with a warning, because it may share code with another patched method and a double
        /// detour kills the game. A method that shares its address with a different method another mod has already patched
        /// is skipped with a warning too. Every shared address is logged with the methods that share it.
        /// </summary>
        internal static List<MethodBase> OnePerAddress(string what, IEnumerable<MethodBase> methods)
        {
            var result = new List<MethodBase>();
            var byAddress = new Dictionary<IntPtr, List<string>>();
            Dictionary<IntPtr, List<KeyValuePair<MethodBase, string>>> foreign = null;
            int refused = 0, skippedForeign = 0;
            foreach (MethodBase m in methods)
            {
                if (m == null) continue;
                string label = m.DeclaringType?.Name + "." + m.Name;
                IntPtr a;
                string problem;
                try { a = TryAddress(m, out problem); }
                catch (Exception e) { a = IntPtr.Zero; problem = "address read failed, " + e.GetType().Name + ": " + e.Message; }
                if (a == IntPtr.Zero)
                {
                    refused++;
                    Note("[NativeMethods] " + what + ": " + label + " not patched, its native address is unknown (" + problem + ").", true);
                    continue;
                }

                foreign ??= ForeignPatched(true);
                string other = null;
                if (foreign.TryGetValue(a, out var patchedBy))
                    foreach (var kv in patchedBy)
                        if (kv.Key != m) { other = kv.Value; break; }
                if (other != null)
                {
                    skippedForeign++;
                    Note("[NativeMethods] " + what + ": " + label + " not patched, its native code at 0x" + a.ToString("X") + " is already patched through " + other + ".", !IsHwe(other));
                    continue;
                }

                if (!byAddress.TryGetValue(a, out List<string> names))
                {
                    byAddress[a] = names = new List<string>();
                    result.Add(m);
                }
                names.Add(m.DeclaringType?.Name ?? "?");
            }
            foreach (var kv in byAddress)
                if (kv.Value.Count > 1)
                    Note("[NativeMethods] " + what + ": " + string.Join(", ", kv.Value) + " share native code at 0x" + kv.Key.ToString("X") + ", patched once.");
            Note("[NativeMethods] " + what + ": " + result.Count + " patch target(s) for " + CountOf(methods) + " method(s), "
                + refused + " refused (address unknown), " + skippedForeign + " skipped (address patched by another mod).");
            return result;
        }

        /// <summary>
        /// Warns about every native address that more than one Harmony-patched method of any mod points at. Each such
        /// pair is a double detour and overflows the stack on its first call. Interop methods only; managed ones are skipped.
        /// </summary>
        internal static void ReportSharedPatchedAddresses()
        {
            var byAddress = new Dictionary<IntPtr, List<string>>();
            var hweOnly = new Dictionary<IntPtr, bool>();
            int checkedCount = 0, unresolved = 0;
            foreach (MethodBase m in HarmonyLib.Harmony.GetAllPatchedMethods())
            {
                try
                {
                    Type t = m.DeclaringType;
                    if (t == null || !typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(t)) continue;
                    IntPtr a = TryAddress(m, out string problem);
                    if (a == IntPtr.Zero)
                    {
                        unresolved++;
                        if (Core.Verbose) Core.Log?.Msg("[NativeMethods] shared-address check skips " + t.Name + "." + m.Name + ": " + problem);
                        continue;
                    }
                    checkedCount++;
                    if (!byAddress.TryGetValue(a, out List<string> names)) byAddress[a] = names = new List<string>();
                    names.Add(t.Name + "." + m.Name);
                    bool onlyHwe = true;
                    HarmonyLib.Patches info2 = HarmonyLib.Harmony.GetPatchInfo(m);
                    if (info2 != null)
                        foreach (string owner in info2.Owners)
                            if (!IsHwe(owner)) { onlyHwe = false; break; }
                    hweOnly[a] = (!hweOnly.TryGetValue(a, out bool before) || before) && onlyHwe;
                }
                catch (Exception e) { Core.Caught("native: read the address of " + m.DeclaringType?.Name + "." + m.Name, e); }
            }
            int shared = 0;
            foreach (var kv in byAddress)
            {
                if (kv.Value.Count < 2) continue;
                shared++;
                string head = "[NativeMethods] patched methods " + string.Join(", ", kv.Value) + " share native code at 0x" + kv.Key.ToString("X");
                if (hweOnly.TryGetValue(kv.Key, out bool hwe) && hwe)
                    Core.Log?.Msg(head + "; all of them are patched by Harder Working Employees only.");
                else
                    Core.Log?.Warning(head + "; each patch detours the same address and the game overflows its stack on the first call.");
            }
            Core.Log?.Msg("[NativeMethods] " + checkedCount + " patched interop method(s) checked, " + shared + " shared native address(es)"
                + (unresolved > 0 ? ", " + unresolved + " without a readable address." : "."));
        }

        private static int CountOf(IEnumerable<MethodBase> methods)
        {
            int n = 0;
            foreach (MethodBase m in methods) if (m != null) n++;
            return n;
        }

        /// <summary>Patches are applied before the mod's logger exists; lines are held until <see cref="Flush"/>.</summary>
        private static void Note(string line, bool warn = false)
        {
            if (Core.Log != null)
            {
                if (warn) Core.Log.Warning(line); else Core.Log.Msg(line);
            }
            else _pending.Add(new KeyValuePair<bool, string>(warn, line));
        }

        internal static void Flush()
        {
            foreach (var line in _pending)
            {
                if (line.Key) Core.Log?.Warning(line.Value);
                else Core.Log?.Msg(line.Value);
            }
            _pending.Clear();
        }
    }
}
