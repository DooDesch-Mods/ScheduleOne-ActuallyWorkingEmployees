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

        /// <summary>Drops in-memory state on save start and scene change.</summary>
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
                    Core.LogDebug("[Features] " + f.Name + " ready.");
                }
                catch (Exception e) { Core.Failed("features: init " + t.Name, e); }
            }
            Core.Log?.Msg("[Features] " + _all.Count + " active: " + string.Join(", ", _all.Select(f => f.Name)));
        }

        internal static void TickAll(float dt)
        {
            if (!Core.InWorld) return;
            for (int i = 0; i < _all.Count; i++)
            {
                IFeature f = _all[i];
                try
                {
#if SNITCH
                    using (Profiler.Sample("AWE." + f.Name)) f.Tick(dt);
#else
                    f.Tick(dt);
#endif
                }
                catch (Exception e) { Core.Failed("features: tick " + f.Name, e); }
            }
        }

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
