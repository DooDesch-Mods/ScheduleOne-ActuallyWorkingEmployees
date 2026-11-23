using System;
using System.Collections.Generic;
using System.Linq;
#if SNITCH
using Snitch.Api;
#endif

namespace ActuallyWorkingEmployees.Features
{
    /// <summary>
    /// One piece of mod behaviour. Implementations are found by reflection, so a new feature is a new file and touches
    /// no shared list. Harmony patches stay in their own [HarmonyPatch] classes; a feature holds the state and ticks.
    /// </summary>
    internal interface IFeature
    {
        /// <summary>Short name for logs, the dev console and the Snitch panel.</summary>
        string Name { get; }

        /// <summary>Runs once from OnInitializeMelon, after the options exist. Register dev commands here.</summary>
        void Init();

        /// <summary>Runs every frame while the Main scene is loaded. Throttle inside; check Net.IsServer for host logic.</summary>
        void Tick(float dt);

        /// <summary>Drops in-memory state on save load and scene change (not on autosave).</summary>
        void Reset();

        /// <summary>One-line live state for 'awe status' and the Snitch panel.</summary>
        string Status { get; }
    }

    internal static class FeatureHost
    {
        private static readonly List<IFeature> _all = new List<IFeature>();
        internal static IReadOnlyList<IFeature> All => _all;

        internal static void Discover()
        {
            Type[] types;
            try { types = typeof(FeatureHost).Assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e)
            {
                Core.Caught("features: GetTypes", e);
                types = e.Types.Where(t => t != null).ToArray();
            }

            foreach (Type t in types.Where(t => !t.IsAbstract && !t.IsInterface && typeof(IFeature).IsAssignableFrom(t)).OrderBy(t => t.FullName))
            {
                IFeature f;
                try { f = (IFeature)Activator.CreateInstance(t, true); }
                catch (Exception e) { Core.Failed("features: create " + t.Name, e); continue; }

                try
                {
                    f.Init();
                    _all.Add(f);
                    if (Core.Verbose) Core.Log?.Msg("[Features] " + f.Name + " ready.");
                }
                catch (Exception e) { Core.Failed("features: init " + t.Name, e); }
            }
            Core.Log?.Msg("[Features] " + _all.Count + " active: " + string.Join(", ", _all.Select(f => f.Name)));
#if DEBUG
            Debugging.DevConsole.Register("perf", "awe perf [seconds]  (time every feature tick for that many real seconds, default 5, then log the frame time and the slowest features)", Perf);
#endif
        }

        internal static void TickAll(float dt)
        {
            if (!Core.InWorld) return;
#if DEBUG
            if (_perfUntil > 0f) PerfFrame();
#endif
            for (int i = 0; i < _all.Count; i++)
            {
                IFeature f = _all[i];
                try
                {
#if DEBUG
                    long t0 = _perfUntil > 0f ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
#endif
#if SNITCH
                    using (Profiler.Sample("AWE." + f.Name)) f.Tick(dt);
#else
                    f.Tick(dt);
#endif
#if DEBUG
                    if (t0 != 0L && i < _perfTicks.Length) _perfTicks[i] += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
#endif
                }
                catch (Exception e) { Core.Failed("features: tick " + f.Name, e); }
            }
        }

#if DEBUG
        // 'awe perf': per-feature tick time, summed over the window, against the real frame time of the same frames.
        private static float _perfUntil;
        private static long[] _perfTicks = new long[0];
        private static int _perfFrames;
        private static float _perfStart;

        private static void Perf(string[] args)
        {
            float seconds = 5f;
            if (args.Length > 0 && !float.TryParse(args[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds))
            {
                Core.Log?.Warning("[Dev] awe perf: '" + args[0] + "' is not a number of seconds.");
                return;
            }
            _perfTicks = new long[_all.Count];
            _perfFrames = 0;
            _perfStart = UnityEngine.Time.realtimeSinceStartup;
            _perfUntil = _perfStart + Math.Max(1f, seconds);
            Core.Log?.Msg("[Dev] [Perf] measuring " + _all.Count + " features for " + Math.Max(1f, seconds).ToString("F0") + " s.");
        }

        private static void PerfFrame()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _perfUntil) { _perfFrames++; return; }
            _perfUntil = 0f;
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int frames = Math.Max(1, _perfFrames);
            double total = _perfTicks.Sum() * toMs;
            double frameMs = (now - _perfStart) * 1000.0 / frames;
            var top = Enumerable.Range(0, Math.Min(_perfTicks.Length, _all.Count))
                .OrderByDescending(i => _perfTicks[i]).Take(6)
                .Select(i => _all[i].Name + "=" + (_perfTicks[i] * toMs / frames).ToString("F2") + "ms");
            Core.Log?.Msg("[Dev] [Perf] " + frames + " frames, frame " + frameMs.ToString("F1") + " ms, AWE ticks " + (total / frames).ToString("F2")
                + " ms/frame. Slowest: " + string.Join(", ", top));
        }
#endif

        internal static void ResetAll()
        {
            foreach (IFeature f in _all)
            {
                try { f.Reset(); }
                catch (Exception e) { Core.Failed("features: reset " + f.Name, e); }
            }
        }
    }
}
