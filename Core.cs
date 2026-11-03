using System;
using System.Collections.Generic;
using MelonLoader;
using S1API.Lifecycle;
using ActuallyWorkingEmployees.Compat;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features;
#if SNITCH
using Snitch.Api;                 // Profiler (Debug + EnableSnitch only; no-op when the host is absent)
#endif

[assembly: MelonInfo(typeof(ActuallyWorkingEmployees.Core), "Actually Working Employees", DooDesch.ModVersion.Current, "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-ActuallyWorkingEmployees")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace ActuallyWorkingEmployees
{
    /// <summary>
    /// MelonLoader entry point. Every option lives in <see cref="Options"/> (MelonPreferences behind it), every piece of
    /// behaviour is an <see cref="IFeature"/> found by reflection. Employee logic runs only on the FishNet server, which
    /// is also true in single-player; a co-op client keeps vanilla behaviour and only reads the host's values.
    /// </summary>
    public sealed class Core : MelonMod
    {
        public static Core Instance { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }

        /// <summary>True while the Main scene is loaded.</summary>
        public static bool InWorld { get; private set; }

        // Keys already reported by Caught / Failed / WarnOnce. Several callers sit in per-frame paths, so each site logs once.
        private static readonly HashSet<string> _reported = new HashSet<string>();

        /// <summary>True while "Verbose log" is on. Check it before building a debug line, so the string is not built when off.</summary>
        public static bool Verbose => Options.VerboseLogging.Value;

        /// <summary>A debug line, written only while <see cref="Verbose"/> is on. Callers that build the string guard with Verbose first.</summary>
        public static void LogDebug(string msg)
        {
            if (Verbose) Log?.Msg(msg);
        }

        /// <summary>
        /// Raised when the log-once keys are cleared (save load, scene change). A helper that keeps its own log-once keys
        /// subscribes and clears them, so a second save logs its messages again.
        /// </summary>
        internal static event Action OnceKeysReset;

        private static void ResetOnceKeys()
        {
            _reported.Clear();
            Action handlers = OnceKeysReset;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception e) { Failed("core: reset log-once keys (" + d.Method.DeclaringType?.Name + ")", e); }
            }
        }

        /// <summary>A caught exception the mod deliberately carries on after. Logs a warning once per <paramref name="where"/>.</summary>
        internal static void Caught(string where, Exception e)
        {
            if (!_reported.Add(where)) return;
            Log?.Warning("[Caught] " + where + ": " + e.GetType().Name + ": " + e.Message);
        }

        /// <summary>A tolerated state without an exception. Logs a warning once per <paramref name="where"/>.</summary>
        internal static void WarnOnce(string where, string message)
        {
            if (!_reported.Add(where)) return;
            Log?.Warning("[" + where + "] " + message);
        }

        /// <summary>A caught exception that made an action fail. Logs an error once per <paramref name="where"/>.</summary>
        internal static void Failed(string where, Exception e)
        {
            if (!_reported.Add(where)) return;
            Log?.Error("[Failed] " + where + ": " + e.GetType().Name + ": " + e.Message);
        }

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;
            NativeMethods.Flush();   // lines of the patch-target resolution, which runs before the logger exists

            Options.Initialize();
            // MelonLoader has applied every [HarmonyPatch] class of this assembly before this method runs. A PatchAll
            // here would apply each a second time, and every postfix would then run twice per call.
            FeatureHost.Discover();

            // Load start and scene change, not OnSaveStart: that one also fires on every autosave, which would wipe live state.
            GameLifecycle.OnPreLoad += ResetState;
            GameLifecycle.OnPreSceneChange += ResetState;

#if SNITCH
            // The Snitch host finds Profiling.SnitchProbe.Register by name, but only after something asks it to look.
            Profiler.AutoRegister();
#endif
#if DEBUG
            Log.Msg($"Actually Working Employees v{DooDesch.ModVersion.Current} (DEBUG), {Options.All.Count} options, {FeatureHost.All.Count} features. Dev console: 'awehelp'.");
#else
            Log.Msg($"Actually Working Employees v{DooDesch.ModVersion.Current} loaded, {Options.All.Count} options.");
#endif
        }

        public override void OnLateInitializeMelon()
        {
            HweDetector.Check();
            NativeMethods.ReportSharedPatchedAddresses();
        }

        /// <summary>Drops every feature's in-memory state and the log-once keys on load start and scene change.</summary>
        private static void ResetState()
        {
            ResetOnceKeys();
            FeatureHost.ResetAll();
            Log?.Msg("[Core] State reset (save load or scene change).");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName != "Main") return;
            InWorld = true;
            Log?.Msg("[Core] Main scene loaded.");
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            if (sceneName != "Main") return;
            InWorld = false;
            ResetState();
        }

        public override void OnUpdate()
        {
            FeatureHost.TickAll(Time.deltaTime);
        }
    }
}
