#if DEBUG
using System;
using System.Reflection;
using ActuallyWorkingEmployees.Compat;
using ActuallyWorkingEmployees.Features;
using ActuallyWorkingEmployees.Features.Balance;
using HarmonyLib;
using Il2CppScheduleOne.ObjectScripts;

namespace ActuallyWorkingEmployees.Debugging
{
    /// <summary>
    /// DEBUG-only checks of methods that share native code with a method another mod patches (AWE#51). Opioid Expansion
    /// patches Pot.GetTemperatureGrowthMultiplier, which the native build folds with DryingRack.GetDryMultiplier.
    ///   addr       native addresses of both methods and the Harmony owners of each.
    ///   shared     reruns the shared-address report of NativeMethods.
    ///   opioid     patches Pot.GetTemperatureGrowthMultiplier with a counting postfix under a foreign Harmony ID, the
    ///              way a mod that loads after AWE does, then calls both methods on a pot and a rack in the world.
    ///   stepback   like opioid, then removes AWE's own patches on the sibling methods at the same address in the same
    ///              call, the order a late step back would run in; then calls both methods.
    ///   status     how often the foreign postfix ran.
    ///   unprobe    removes the foreign patch.
    ///   siblings   for every method AWE patched, the game methods that share its native code.
    /// </summary>
    internal sealed class NativeProbe : IFeature
    {
        public string Name => "native";

        internal const string ProbeOwner = "Probe.OpioidExpansionSim";
        private static int _hits;
        private HarmonyLib.Harmony _probe;

        public void Init()
        {
            DevConsole.Register("native", "awe native <addr|shared|opioid|stepback|status|unprobe|siblings>  (methods that share native code, AWE#51)", Dev);
        }

        public void Reset() { }

        public void Tick(float dt) { }

        public string Status => "probe=" + (_probe != null) + " hits=" + _hits;

        private static MethodBase DryMethod => AccessTools.Method(typeof(DryingRack), nameof(DryingRack.GetDryMultiplier));
        private static MethodBase PotMethod => AccessTools.Method(typeof(Pot), nameof(Pot.GetTemperatureGrowthMultiplier));

        private void Dev(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            try
            {
                if (mode == "addr") Addr();
                else if (mode == "shared") NativeMethods.ReportSharedPatchedAddresses();
                else if (mode == "opioid") { Probe(); Call("opioid"); }
                else if (mode == "stepback") { Probe(); StepBack(); Call("stepback"); }
                else if (mode == "unprobe") Unprobe();
                else if (mode == "siblings") Siblings();
                else Core.Log?.Msg("[Dev] [native] status: " + Status + ".");
            }
            catch (Exception e) { Core.Failed("native " + mode, e); }
        }

        private static string Owners(MethodBase m)
        {
            var info = HarmonyLib.Harmony.GetPatchInfo(m);
            return info == null || info.Owners.Count == 0 ? "none" : string.Join(", ", info.Owners);
        }

        private static void Addr()
        {
            IntPtr dry = NativeMethods.Address(DryMethod), pot = NativeMethods.Address(PotMethod);
            Core.Log?.Msg("[Dev] [native] addr: DryingRack.GetDryMultiplier=0x" + dry.ToString("X") + " (owners " + Owners(DryMethod)
                + ") Pot.GetTemperatureGrowthMultiplier=0x" + pot.ToString("X") + " (owners " + Owners(PotMethod) + ") shared=" + (dry != IntPtr.Zero && dry == pot) + ".");
        }

        private void Probe()
        {
            if (_probe != null) { Core.Log?.Msg("[Dev] [native] probe already applied."); return; }
            _probe = new HarmonyLib.Harmony(ProbeOwner);
            var postfix = new HarmonyMethod(typeof(NativeProbe).GetMethod(nameof(ProbePostfix), BindingFlags.NonPublic | BindingFlags.Static));
            _probe.Patch(PotMethod, postfix: postfix);
            Core.Log?.Msg("[Dev] [native] probe: patched Pot.GetTemperatureGrowthMultiplier as " + ProbeOwner + "; owners now " + Owners(PotMethod) + ".");
        }

        private static void ProbePostfix() { _hits++; }

        /// <summary>Removes AWE's own patches from every method that shares the probed address, the way a late step back would.</summary>
        private static void StepBack()
        {
            IntPtr a = NativeMethods.Address(PotMethod);
            var own = Core.Instance.HarmonyInstance;
            int removed = 0;
            foreach (MethodBase m in HarmonyLib.Harmony.GetAllPatchedMethods())
            {
                if (m == PotMethod || NativeMethods.Address(m) != a) continue;
                var info = HarmonyLib.Harmony.GetPatchInfo(m);
                if (info == null || !info.Owners.Contains(own.Id)) continue;
                own.Unpatch(m, HarmonyPatchType.All, own.Id);
                removed++;
                Core.Log?.Msg("[Dev] [native] stepback: removed AWE's patches on " + m.DeclaringType?.Name + "." + m.Name + "; owners now " + Owners(m) + ".");
            }
            Core.Log?.Msg("[Dev] [native] stepback: " + removed + " method(s) unpatched at 0x" + a.ToString("X") + ".");
        }

        private static void Call(string tag)
        {
            var pot = UnityEngine.Object.FindObjectOfType<Pot>();
            var rack = UnityEngine.Object.FindObjectOfType<DryingRack>();
            int before = _hits;
            string p = pot == null ? "no pot" : Eff.F(pot.GetTemperatureGrowthMultiplier());
            string r = rack == null ? "no rack" : Eff.F(rack.GetDryMultiplier());
            Core.Log?.Msg("[Dev] [native] " + tag + ": calls returned, pot=" + p + " rack=" + r + " probeHits+" + (_hits - before) + ".");
        }

        /// <summary>
        /// For every method AWE has patched, the other game methods (Assembly-CSharp) whose native code is the same
        /// function. A mod that patches one of those after AWE detours the address a second time.
        /// </summary>
        private static void Siblings()
        {
            string own = Core.Instance.HarmonyInstance.Id;
            var ownAt = new System.Collections.Generic.Dictionary<IntPtr, string>();
            foreach (MethodBase m in HarmonyLib.Harmony.GetAllPatchedMethods())
            {
                var info = HarmonyLib.Harmony.GetPatchInfo(m);
                if (info == null || !info.Owners.Contains(own)) continue;
                IntPtr a = NativeMethods.AddressQuiet(m, out string problem);
                if (a == IntPtr.Zero) { Core.Log?.Warning("[Dev] [native] siblings: AWE target " + m.DeclaringType?.Name + "." + m.Name + " has no readable address (" + problem + ")."); continue; }
                ownAt[a] = (ownAt.TryGetValue(a, out string s) ? s + ", " : "") + m.DeclaringType?.Name + "." + m.Name;
            }
            var found = new System.Collections.Generic.Dictionary<IntPtr, System.Collections.Generic.List<string>>();
            int scanned = 0, unreadable = 0, typeErrors = 0;
            var firstProblems = new System.Collections.Generic.List<string>();
            Type[] types;
            try { types = typeof(Pot).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                types = Array.FindAll(e.Types, t => t != null);
                Core.Log?.Warning("[Dev] [native] siblings: " + (e.Types.Length - types.Length) + " type(s) of Assembly-CSharp did not load and are not scanned (" + e.Message + ").");
            }
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (Type t in types)
            {
                if (t.IsGenericTypeDefinition) continue;
                MethodInfo[] methods;
                try { methods = t.GetMethods(all); }
                catch (Exception e)
                {
                    typeErrors++;
                    if (firstProblems.Count < 3) firstProblems.Add(t.Name + ": methods not listed, " + e.GetType().Name + ": " + e.Message);
                    continue;
                }
                foreach (MethodInfo m in methods)
                {
                    if (m.IsGenericMethodDefinition || m.IsAbstract) continue;
                    IntPtr a = NativeMethods.AddressQuiet(m, out string problem);
                    if (a == IntPtr.Zero)
                    {
                        // Managed helpers of the interop types have no NativeMethodInfoPtr_ field; they are counted, not listed.
                        unreadable++;
                        if (firstProblems.Count < 3) firstProblems.Add(problem);
                        continue;
                    }
                    scanned++;
                    if (!ownAt.ContainsKey(a)) continue;
                    if (!found.TryGetValue(a, out var list)) found[a] = list = new System.Collections.Generic.List<string>();
                    list.Add(t.Name + "." + m.Name);
                }
            }
            foreach (var kv in ownAt)
            {
                var list = found.TryGetValue(kv.Key, out var l) ? l : new System.Collections.Generic.List<string>();
                Core.Log?.Msg("[Dev] [native] siblings: 0x" + kv.Key.ToString("X") + " AWE patches " + kv.Value + "; " + list.Count + " method(s) share it"
                    + (list.Count > 1 ? ": " + string.Join(", ", list) : "") + ".");
            }
            Core.Log?.Msg("[Dev] [native] siblings: " + ownAt.Count + " AWE address(es), " + scanned + " game method(s) scanned, " + unreadable
                + " without a readable address, " + typeErrors + " type(s) not listed" + (firstProblems.Count > 0 ? "; first: " + string.Join(" | ", firstProblems) : "") + ".");
        }

        private void Unprobe()
        {
            if (_probe == null) { Core.Log?.Msg("[Dev] [native] unprobe: no probe applied."); return; }
            _probe.UnpatchSelf();
            _probe = null;
            Core.Log?.Msg("[Dev] [native] unprobe: removed; owners now " + Owners(PotMethod) + ".");
        }
    }
}
#endif
