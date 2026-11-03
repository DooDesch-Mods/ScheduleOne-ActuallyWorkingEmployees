using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppScheduleOne.Management;
using ActuallyWorkingEmployees.Config;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.Reliability
{
    /// <summary>
    /// Fix 6: employee path checks are cached.
    ///
    /// Each idle employee asks NPCMovement.CanGetTo for every station and route on every 0.5 s tick, and each call
    /// computes a NavMesh path. Employee answers are kept for the configured time per employee, target and required
    /// proximity. The employee's own position is part of the key at a 2 m grid, so a walking employee asks again.
    /// Nothing scans the scene; the cache is a dictionary that is cleared on save start and when it grows past 4096.
    /// </summary>
    internal sealed class Fix6ReachCache : IFeature
    {
        internal const string FeatureName = "fix6-reach-cache";
        public string Name => FeatureName;

        internal struct Key : IEquatable<Key>
        {
            internal int Npc; internal long Target; internal int Cell; internal int Prox;
            public bool Equals(Key o) { return Npc == o.Npc && Target == o.Target && Cell == o.Cell && Prox == o.Prox; }
            public override bool Equals(object o) { return o is Key k && Equals(k); }
            public override int GetHashCode() { return HashCode.Combine(Npc, Target, Cell, Prox); }
        }

        private struct Entry { internal bool Result; internal float Until; }

        private static readonly Dictionary<Key, Entry> _cache = new Dictionary<Key, Entry>();
        private static int _hits, _misses;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("reach", "awe reach [clear|test]  (cache counters / empty it / ask the same path twice)", Dev);
#endif
        }

        public void Reset() { _cache.Clear(); }
        public void Tick(float dt) { }

        public string Status =>
            "on=" + (Options.CacheReachability.Value ? 1 : 0) + " hits=" + _hits + " misses=" + _misses + " entries=" + _cache.Count;

        private static Key MakeKey(NPCMovement m, long target, float prox)
        {
            Vector3 p = m.transform.position;
            int cell = HashCode.Combine((int)Math.Floor(p.x / 2f), (int)Math.Floor(p.y / 2f), (int)Math.Floor(p.z / 2f));
            return new Key { Npc = m.GetInstanceID(), Target = target, Cell = cell, Prox = (int)Math.Round(prox * 10f) };
        }

        private static long Quantize(Vector3 v)
        {
            // 0.5 m grid on each axis, packed into 21 bits per axis.
            long x = (long)Math.Round(v.x * 2f) & 0x1FFFFF, y = (long)Math.Round(v.y * 2f) & 0x1FFFFF, z = (long)Math.Round(v.z * 2f) & 0x1FFFFF;
            return (x << 42) | (y << 21) | z;
        }

        /// <summary>Prefix body. True (and <paramref name="result"/> set) when a fresh cached answer exists; <paramref name="key"/> is set on a miss.</summary>
        internal static bool TryGet(NPCMovement m, long target, float prox, out bool result, out object key)
        {
            result = false; key = null;
            if (!Rel.Gate(FeatureName, Options.CacheReachability.Value)) return false;
            Key k = MakeKey(m, target, prox);
            if (_cache.TryGetValue(k, out Entry e) && e.Until > Time.time) { _hits++; result = e.Result; return true; }
            _misses++;
            key = k;
            return false;
        }

        /// <summary>Postfix body: stores the answer under the key the prefix handed over (null when the cache was bypassed).</summary>
        internal static void Store(object key, bool result)
        {
            if (key == null) return;
            if (_cache.Count >= 4096) _cache.Clear();
            _cache[(Key)key] = new Entry { Result = result, Until = Time.time + Options.ReachabilityCacheSeconds.Value };
        }

        internal static long Target(Vector3 v) { return Quantize(v); }
        internal static long Target(ITransitEntity e) { return e == null ? 0 : e.Pointer.ToInt64(); }

#if DEBUG
        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("reach")) return;
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (mode == "clear")
            {
                int n = _cache.Count;
                _cache.Clear();
                Core.Log?.Msg("[Dev] [Fix6] cleared " + n + " cached path check(s).");
            }
            else if (mode == "test")
            {
                foreach (Employee emp in Rel.Employees())
                {
                    Vector3 target = emp.transform.position + emp.transform.forward * 3f;
                    int h0 = _hits, m0 = _misses;
                    bool a = emp.Movement.CanGetTo(target, 1f);
                    bool b = emp.Movement.CanGetTo(target, 1f);
                    Core.Log?.Msg("[Dev] [Fix6] " + Rel.Name(emp) + ": CanGetTo twice -> " + a + "/" + b + ", hits +" + (_hits - h0) + ", misses +" + (_misses - m0));
                    break;
                }
            }
            else if (mode != "") Core.Log?.Warning("[Dev] awe reach: unknown mode '" + mode + "'.");
            Core.Log?.Msg("[Dev] [Fix6] " + new Fix6ReachCache().Status);
        }
#endif
    }

    /// <summary>NPCMovement.CanGetTo(Vector3, float) (Il2CppScheduleOne.NPCs/NPCMovement.cs:3228).</summary>
    [HarmonyPatch]
    internal static class Awe_Fix6_CanGetToPosition_Patch
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.DeclaredMethod(typeof(NPCMovement), nameof(NPCMovement.CanGetTo), new Type[] { typeof(Vector3), typeof(float) });
        }

        private static bool Prefix(NPCMovement __instance, Vector3 position, float proximityReq, ref bool __result, out object __state)
        {
            __state = null;
            try
            {
                if (Fix6ReachCache.TryGet(__instance, Fix6ReachCache.Target(position), proximityReq, out bool cached, out object key)) { __result = cached; return false; }
                __state = key;
            }
            catch (Exception e) { Core.Failed("fix6: CanGetTo(Vector3) prefix", e); }
            return true;
        }

        private static void Postfix(bool __result, object __state)
        {
            try { Fix6ReachCache.Store(__state, __result); }
            catch (Exception e) { Core.Failed("fix6: CanGetTo(Vector3) postfix", e); }
        }
    }

    /// <summary>NPCMovement.CanGetTo(ITransitEntity, float) (Il2CppScheduleOne.NPCs/NPCMovement.cs:3242).</summary>
    [HarmonyPatch]
    internal static class Awe_Fix6_CanGetToEntity_Patch
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.DeclaredMethod(typeof(NPCMovement), nameof(NPCMovement.CanGetTo), new Type[] { typeof(ITransitEntity), typeof(float) });
        }

        private static bool Prefix(NPCMovement __instance, ITransitEntity destination, float proximityReq, ref bool __result, out object __state)
        {
            __state = null;
            try
            {
                if (Fix6ReachCache.TryGet(__instance, Fix6ReachCache.Target(destination), proximityReq, out bool cached, out object key)) { __result = cached; return false; }
                __state = key;
            }
            catch (Exception e) { Core.Failed("fix6: CanGetTo(ITransitEntity) prefix", e); }
            return true;
        }

        private static void Postfix(bool __result, object __state)
        {
            try { Fix6ReachCache.Store(__state, __result); }
            catch (Exception e) { Core.Failed("fix6: CanGetTo(ITransitEntity) postfix", e); }
        }
    }
}
