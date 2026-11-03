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
        private static readonly List<string> _pending = new List<string>();

        /// <summary>
        /// Address of the native function that <paramref name="m"/> calls, read from its NativeMethodInfoPtr_ field
        /// (Il2CppMethodInfo.methodPointer is the struct's first field). IntPtr.Zero when the field is not found.
        /// </summary>
        internal static IntPtr Address(MethodBase m)
        {
            Type t = m?.DeclaringType;
            if (t == null) return IntPtr.Zero;
            string prefix = "NativeMethodInfoPtr_" + m.Name + "_";
            FieldInfo found = null;
            foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (!f.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (found != null) { Note("[NativeMethods] " + t.Name + "." + m.Name + " has several overloads, address not resolved."); return IntPtr.Zero; }
                found = f;
            }
            if (found == null) { Note("[NativeMethods] " + t.Name + "." + m.Name + " has no NativeMethodInfoPtr_ field, address not resolved."); return IntPtr.Zero; }
            IntPtr info = (IntPtr)found.GetValue(null);
            return info == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(info);
        }

        /// <summary>
        /// <paramref name="methods"/> reduced to one method per native address, in the given order. A method whose
        /// address cannot be read is kept. Every shared address is logged with the methods that share it.
        /// </summary>
        internal static List<MethodBase> OnePerAddress(string what, IEnumerable<MethodBase> methods)
        {
            var result = new List<MethodBase>();
            var byAddress = new Dictionary<IntPtr, List<string>>();
            foreach (MethodBase m in methods)
            {
                if (m == null) continue;
                IntPtr a;
                try { a = Address(m); }
                catch (Exception e) { Note("[NativeMethods] " + m.DeclaringType?.Name + "." + m.Name + ": address read failed, " + e.GetType().Name + ": " + e.Message); a = IntPtr.Zero; }
                if (a == IntPtr.Zero) { result.Add(m); continue; }
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
            Note("[NativeMethods] " + what + ": " + result.Count + " patch target(s) for " + CountOf(methods) + " method(s).");
            return result;
        }

        /// <summary>
        /// Warns about every native address that more than one Harmony-patched method of any mod points at. Each such
        /// pair is a double detour and overflows the stack on its first call. Interop methods only; managed ones are skipped.
        /// </summary>
        internal static void ReportSharedPatchedAddresses()
        {
            var byAddress = new Dictionary<IntPtr, List<string>>();
            int checkedCount = 0;
            foreach (MethodBase m in HarmonyLib.Harmony.GetAllPatchedMethods())
            {
                FieldInfo f = null;
                try
                {
                    Type t = m.DeclaringType;
                    if (t == null || !typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(t)) continue;
                    string prefix = "NativeMethodInfoPtr_" + m.Name + "_";
                    int hits = 0;
                    foreach (FieldInfo cand in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                        if (cand.Name.StartsWith(prefix, StringComparison.Ordinal)) { f = cand; hits++; }
                    if (hits != 1) continue;   // overloads: the field cannot be told apart by name
                    IntPtr info = (IntPtr)f.GetValue(null);
                    if (info == IntPtr.Zero) continue;
                    IntPtr a = Marshal.ReadIntPtr(info);
                    checkedCount++;
                    if (!byAddress.TryGetValue(a, out List<string> names)) byAddress[a] = names = new List<string>();
                    names.Add(t.Name + "." + m.Name);
                }
                catch (Exception e) { Core.Caught("native: read the address of " + m.DeclaringType?.Name + "." + m.Name, e); }
            }
            int shared = 0;
            foreach (var kv in byAddress)
            {
                if (kv.Value.Count < 2) continue;
                shared++;
                Core.Log?.Warning("[NativeMethods] patched methods " + string.Join(", ", kv.Value) + " share native code at 0x" + kv.Key.ToString("X") + "; each patch detours the same address and the game overflows its stack on the first call.");
            }
            Core.Log?.Msg("[NativeMethods] " + checkedCount + " patched interop method(s) checked, " + shared + " shared native address(es).");
        }

        private static int CountOf(IEnumerable<MethodBase> methods)
        {
            int n = 0;
            foreach (MethodBase m in methods) if (m != null) n++;
            return n;
        }

        /// <summary>Patches are applied before the mod's logger exists; lines are held until <see cref="Flush"/>.</summary>
        private static void Note(string line)
        {
            if (Core.Log != null) Core.Log.Msg(line);
            else _pending.Add(line);
        }

        internal static void Flush()
        {
            foreach (string line in _pending) Core.Log?.Msg(line);
            _pending.Clear();
        }
    }
}
