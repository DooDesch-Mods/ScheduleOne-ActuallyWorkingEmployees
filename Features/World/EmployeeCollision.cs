using System;
using System.Collections.Generic;
using UnityEngine.AI;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features.Reliability;
using ActuallyWorkingEmployees.Networking;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.World
{
    /// <summary>
    /// Employees that walk through each other (DisableEmployeeCollision) and through furniture (DisableFurnitureCollision).
    ///
    /// Employees: every collider under an employee becomes a trigger, and its NavMeshAgent shrinks to a small radius
    /// without obstacle avoidance, so agents no longer push each other aside. Furniture: every NavMeshObstacle under a
    /// placed buildable item stops carving the NavMesh, so paths run through it. Both are host-side only; the host
    /// simulates every employee. What a switch changed is remembered and written back when it goes off again.
    /// </summary>
    internal sealed class EmployeeCollision : IFeature
    {
        internal const string FeatureName = "collision";
        public string Name => FeatureName;

        private const float AgentRadius = 0.15f;
        private const float EmployeePass = 1f;       // seconds between employee passes
        private const float FurniturePass = 5f;      // seconds between furniture passes
        private const float FurnitureRescan = 30f;   // a full rescan even when the item count did not change

        private struct AgentOriginal { internal float Radius; internal int Priority; internal ObstacleAvoidanceType Avoidance; }

        // Employees seen by the last pass (instance id -> changes made). Colliders that were triggers before stay as they are.
        private readonly Dictionary<int, List<UnityEngine.Collider>> _changedColliders = new Dictionary<int, List<UnityEngine.Collider>>();
        private readonly Dictionary<int, AgentOriginal> _agents = new Dictionary<int, AgentOriginal>();
        private readonly Dictionary<int, UnityEngine.Component> _agentOwner = new Dictionary<int, UnityEngine.Component>();

        private readonly List<NavMeshObstacle> _carving = new List<NavMeshObstacle>();   // obstacles this feature switched off
        private readonly HashSet<int> _scannedItems = new HashSet<int>();
        private bool _empWas, _furnWas;
        private int _lastItemCount = -1, _obstaclesSeen;
        private float _nextEmp, _nextFurn, _nextRescan;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("collision", "awe collision  (run both passes now and print what they hold)", Dev);
#endif
        }

        public void Reset()
        {
            // The scene is gone, and so are the objects these lists point at.
            _changedColliders.Clear();
            _agents.Clear();
            _agentOwner.Clear();
            _carving.Clear();
            _scannedItems.Clear();
            _empWas = _furnWas = false;
            _lastItemCount = -1;
            _obstaclesSeen = 0;
            _nextEmp = _nextFurn = _nextRescan = 0f;
        }

        public string Status =>
            "employees=" + (Options.DisableEmployeeCollision.Value ? 1 : 0) + " furniture=" + (Options.DisableFurnitureCollision.Value ? 1 : 0)
            + " triggerEmployees=" + _changedColliders.Count + " agents=" + _agents.Count + " obstaclesOff=" + _carving.Count
            + " obstaclesSeen=" + _obstaclesSeen;

        public void Tick(float dt)
        {
            float now = UnityEngine.Time.unscaledTime;
            bool emp = Options.DisableEmployeeCollision.Value;
            bool furn = Options.DisableFurnitureCollision.Value;
            if (!emp && !_empWas && !furn && !_furnWas) return;
            if (!Net.IsServer)
            {
                Rel.MsgOnce("collision:client", "[collision] not the host, vanilla collision on this client.");
                return;
            }
            if (now >= _nextEmp && (emp || _empWas)) { _nextEmp = now + EmployeePass; EmployeePassNow(emp); }
            if (now >= _nextFurn && (furn || _furnWas)) { _nextFurn = now + FurniturePass; FurniturePassNow(furn, now); }
        }

        private void EmployeePassNow(bool on)
        {
            if (!on)
            {
                RestoreEmployees();
                _empWas = false;
                return;
            }
            if (!_empWas) Core.Log?.Msg("[collision] employees pass through each other.");
            _empWas = true;

            foreach (Employee e in Rel.Employees())
            {
                int id = e.GetInstanceID();
                if (_changedColliders.ContainsKey(id)) continue;   // done once per employee
                var changed = new List<UnityEngine.Collider>();
                var colliders = e.GetComponentsInChildren<UnityEngine.Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    var c = colliders[i];
                    if (c == null || c.isTrigger) continue;
                    c.isTrigger = true;
                    changed.Add(c);
                }
                _changedColliders[id] = changed;

                var agent = e.GetComponent<NavMeshAgent>();
                if (agent == null) { Core.WarnOnce("collision:noagent:" + id, Rel.Name(e) + " has no NavMeshAgent; only its colliders were changed."); continue; }
                _agents[id] = new AgentOriginal { Radius = agent.radius, Priority = agent.avoidancePriority, Avoidance = agent.obstacleAvoidanceType };
                _agentOwner[id] = agent;
                if (agent.radius > AgentRadius) agent.radius = AgentRadius;
                agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
                agent.avoidancePriority = 0;
            }
        }

        private void RestoreEmployees()
        {
            int restored = 0;
            foreach (var kv in _changedColliders)
                foreach (var c in kv.Value)
                    if (c != null) { c.isTrigger = false; restored++; }
            foreach (var kv in _agents)
            {
                if (!_agentOwner.TryGetValue(kv.Key, out UnityEngine.Component owner)) continue;
                var agent = owner.TryCast<NavMeshAgent>();
                if (agent == null) continue;
                agent.radius = kv.Value.Radius;
                agent.avoidancePriority = kv.Value.Priority;
                agent.obstacleAvoidanceType = kv.Value.Avoidance;
            }
            Core.Log?.Msg("[collision] employee collision restored on " + _changedColliders.Count + " employee(s), " + restored + " collider(s).");
            _changedColliders.Clear();
            _agents.Clear();
            _agentOwner.Clear();
        }

        private void FurniturePassNow(bool on, float now)
        {
            if (!on)
            {
                int back = 0;
                for (int i = 0; i < _carving.Count; i++)
                    if (_carving[i] != null) { _carving[i].carving = true; back++; }
                Core.Log?.Msg("[collision] furniture carving restored on " + back + " obstacle(s).");
                _carving.Clear();
                _scannedItems.Clear();
                _lastItemCount = -1;
                _furnWas = false;
                return;
            }
            if (!_furnWas) Core.Log?.Msg("[collision] employees ignore furniture colliders.");
            _furnWas = true;

            int total = CountItems();
            if (total == _lastItemCount && now < _nextRescan) return;   // nothing placed or removed since the last scan
            _lastItemCount = total;
            _nextRescan = now + FurnitureRescan;
            int before = _carving.Count;
            ScanSources();
            if (_carving.Count != before)
                Core.Log?.Msg("[collision] " + (_carving.Count - before) + " more obstacle(s) stopped carving (" + _carving.Count + " in total, " + _obstaclesSeen + " found).");
        }

        private static int CountItems()
        {
            int n = 0;
            var props = Il2CppScheduleOne.Property.Property.Properties;
            if (props != null)
                for (int i = 0; i < props.Count; i++) n += props[i]?.BuildableItems?.Count ?? 0;
            var biz = Il2CppScheduleOne.Property.Business.Businesses;
            if (biz != null)
                for (int i = 0; i < biz.Count; i++) n += biz[i]?.BuildableItems?.Count ?? 0;
            return n;
        }

        private void ScanSources()
        {
            var props = Il2CppScheduleOne.Property.Property.Properties;
            if (props != null)
                for (int i = 0; i < props.Count; i++) ScanItems(props[i]?.BuildableItems);
            var biz = Il2CppScheduleOne.Property.Business.Businesses;
            if (biz != null)
                for (int i = 0; i < biz.Count; i++) ScanItems(biz[i]?.BuildableItems);
        }

        private void ScanItems(Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.EntityFramework.BuildableItem> items)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || !_scannedItems.Add(item.GetInstanceID())) continue;
                var obstacles = item.GetComponentsInChildren<NavMeshObstacle>(true);
                for (int k = 0; k < obstacles.Length; k++)
                {
                    var o = obstacles[k];
                    if (o == null) continue;
                    _obstaclesSeen++;
                    if (!o.carving) continue;
                    o.carving = false;
                    _carving.Add(o);
                }
            }
        }

#if DEBUG
        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("collision")) return;
            foreach (IFeature f in FeatureHost.All)
            {
                if (!(f is EmployeeCollision c)) continue;
                float now = UnityEngine.Time.unscaledTime;
                c.EmployeePassNow(Options.DisableEmployeeCollision.Value);
                c.FurniturePassNow(Options.DisableFurnitureCollision.Value, now);
                c._nextEmp = c._nextFurn = now + 1f;
                Core.Log?.Msg("[Dev] [Collision] " + c.Status);
                return;
            }
            Core.Log?.Warning("[Dev] [Collision] the feature is not registered.");
        }
#endif
    }
}
