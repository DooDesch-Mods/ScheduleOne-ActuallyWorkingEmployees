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

        public void Reset() { _cache.Clear(); _failures = 0; _retryAt = 0f; _backoff = 0f; }
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

        // After repeated exceptions in the cache code the cache is bypassed for a growing time and then tried again.
        private const int FailuresBeforeBackoff = 3;
        private static int _failures;
        private static float _retryAt, _backoff;

        /// <summary>True while the cache is switched off after repeated failures. Logs when the pause ends.</summary>
        private static bool BackedOff()
        {
            if (_retryAt <= 0f) return false;
            if (Time.time < _retryAt) return true;
            _retryAt = 0f;
            _failures = 0;
            Core.Log?.Msg("[Fix6] the path cache is active again after a pause of " + _backoff.ToString("F0") + " s.");
            return false;
        }

        /// <summary>Counts an exception of the cache code; at the third in a row the cache pauses and the pause grows each time (30 s up to 5 min).</summary>
        internal static void Failure(string where, Exception e)
        {
            Core.Failed(where, e);
            if (++_failures < FailuresBeforeBackoff) return;
            _backoff = _backoff <= 0f ? 30f : Math.Min(_backoff * 2f, 300f);
            _retryAt = Time.time + _backoff;
            _cache.Clear();
            Core.Log?.Warning("[Fix6] " + _failures + " failures in a row, the path cache is bypassed for " + _backoff.ToString("F0") + " s and then tried again.");
        }

        /// <summary>Prefix body. True (and <paramref name="result"/> set) when a fresh cached answer exists; <paramref name="key"/> is valid when <paramref name="hasKey"/> is set (a miss).</summary>
        internal static bool TryGet(NPCMovement m, long target, float prox, out bool result, out Key key, out bool hasKey)
        {
            result = false; key = default; hasKey = false;
            if (BackedOff()) return false;
            if (!Rel.Gate(FeatureName, Options.CacheReachability.Value)) return false;
            Key k = MakeKey(m, target, prox);
            if (_cache.TryGetValue(k, out Entry e) && e.Until > Time.time) { _hits++; result = e.Result; return true; }
            _misses++;
            key = k;
            hasKey = true;
            return false;
        }

        /// <summary>Postfix body: stores the answer under the key the prefix handed over.</summary>
        internal static void Store(Key key, bool result)
        {
            if (_cache.Count >= 4096) _cache.Clear();
            _cache[key] = new Entry { Result = result, Until = Time.time + Options.ReachabilityCacheSeconds.Value };
            _failures = 0;
            _backoff = 0f;
        }

        /// <summary>Per-call state from prefix to postfix and finalizer. Entered: this call holds the re-entry guard. HasKey: Key is the miss to store.</summary>
        internal struct Call { internal bool Entered; internal bool HasKey; internal Key Key; }

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

        private const string Guard = "fix6.cangetto.position";

        private static bool Prefix(NPCMovement __instance, Vector3 position, float proximityReq, ref bool __result, out Fix6ReachCache.Call __state)
        {
            __state = default;
            if (!Rel.Enter(Guard)) return true;
            __state.Entered = true;
            try
            {
                if (Fix6ReachCache.TryGet(__instance, Fix6ReachCache.Target(position), proximityReq, out bool cached, out Fix6ReachCache.Key key, out bool hasKey)) { __result = cached; return false; }
                __state.Key = key;
                __state.HasKey = hasKey;
            }
            catch (Exception e) { Fix6ReachCache.Failure("fix6: CanGetTo(Vector3) prefix", e); }
            return true;
        }

        private static void Postfix(bool __result, Fix6ReachCache.Call __state)
        {
            if (!__state.HasKey) return;
            try { Fix6ReachCache.Store(__state.Key, __result); }
            catch (Exception e) { Fix6ReachCache.Failure("fix6: CanGetTo(Vector3) postfix", e); }
        }

        // Runs even when the original throws, so the re-entry guard cannot stay set and bypass the cache for the rest of the session.
        private static Exception Finalizer(Fix6ReachCache.Call __state, Exception __exception)
        {
            if (__state.Entered) Rel.Leave(Guard);
            return __exception;
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

        private const string Guard = "fix6.cangetto.entity";

        // Parameter names must match the interop signature CanGetTo(ITransitEntity entity, float proximityReq).
        private static bool Prefix(NPCMovement __instance, ITransitEntity entity, float proximityReq, ref bool __result, out Fix6ReachCache.Call __state)
        {
            __state = default;
            if (!Rel.Enter(Guard)) return true;
            __state.Entered = true;
            try
            {
                if (Fix6ReachCache.TryGet(__instance, Fix6ReachCache.Target(entity), proximityReq, out bool cached, out Fix6ReachCache.Key key, out bool hasKey)) { __result = cached; return false; }
                __state.Key = key;
                __state.HasKey = hasKey;
            }
            catch (Exception e) { Fix6ReachCache.Failure("fix6: CanGetTo(ITransitEntity) prefix", e); }
            return true;
        }

        private static void Postfix(bool __result, Fix6ReachCache.Call __state)
        {
            if (!__state.HasKey) return;
            try { Fix6ReachCache.Store(__state.Key, __result); }
            catch (Exception e) { Fix6ReachCache.Failure("fix6: CanGetTo(ITransitEntity) postfix", e); }
        }

        private static Exception Finalizer(Fix6ReachCache.Call __state, Exception __exception)
        {
            if (__state.Entered) Rel.Leave(Guard);
            return __exception;
        }
    }
}
