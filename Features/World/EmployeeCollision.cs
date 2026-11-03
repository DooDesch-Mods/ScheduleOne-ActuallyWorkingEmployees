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
    /// Employees: the root capsule of NPCMovement becomes a trigger (the ragdoll bone colliders stay solid, so a knocked
    /// down employee still lands on the floor), its NavMeshAgent shrinks to a small radius, and
    /// obstacle avoidance goes off through NPCMovement.SetObstacleAvoidanceEnabled(false). Setting the agent's avoidance
    /// type alone does not hold: NPCMovement.UpdateObstacleAvoidance runs every 0.5 s on the host and puts the default
    /// type back whenever a player is within 25 m and the movement's own flag is on.
    /// Furniture: every NavMeshObstacle under a placed buildable item is disabled, as BuildManager.DisableNavigation does
    /// for the build preview. Carving off alone is not enough: a non-carving obstacle is still a moving obstacle that local
    /// avoidance steers around, and tightly placed lab ovens and cauldrons leave no way between them.
    /// Both are host-side only; the host simulates every employee. What a switch changed is remembered and written back
    /// when it goes off again.
    /// </summary>
    internal sealed class EmployeeCollision : IFeature
    {
        internal const string FeatureName = "collision";
        public string Name => FeatureName;

        private const float AgentRadius = 0.15f;
        private const float EmployeePass = 1f;       // seconds between employee passes
        private const float FurniturePass = 5f;      // seconds between furniture passes
        private const float FurnitureRescan = 30f;   // a full rescan even when the item count did not change

        private struct AgentOriginal { internal float Radius; internal int Priority; internal ObstacleAvoidanceType Avoidance; internal bool AvoidanceFlag; }

        // Employees seen by the last pass (instance id -> changes made). Colliders that were triggers before stay as they are.
        private readonly Dictionary<int, List<UnityEngine.Collider>> _changedColliders = new Dictionary<int, List<UnityEngine.Collider>>();
        private readonly Dictionary<int, AgentOriginal> _agents = new Dictionary<int, AgentOriginal>();
        private readonly Dictionary<int, UnityEngine.Component> _agentOwner = new Dictionary<int, UnityEngine.Component>();
        private readonly Dictionary<int, Il2CppScheduleOne.NPCs.NPCMovement> _movement = new Dictionary<int, Il2CppScheduleOne.NPCs.NPCMovement>();

        private readonly List<NavMeshObstacle> _disabled = new List<NavMeshObstacle>();   // obstacles this feature switched off
        private readonly HashSet<int> _scannedItems = new HashSet<int>();
        private bool _empWas, _furnWas;
        private int _lastItemCount = -1, _obstaclesSeen;
        private float _nextEmp, _nextFurn, _nextRescan;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("collision", "awe collision [ragdoll|probe]  (run both passes now and print what they hold; ragdoll knocks one employee down, probe reports it)", Dev);
#endif
        }

        public void Reset()
        {
            // The scene is gone, and so are the objects these lists point at.
            _changedColliders.Clear();
            _agents.Clear();
            _agentOwner.Clear();
            _movement.Clear();
            _disabled.Clear();
            _scannedItems.Clear();
            _empWas = _furnWas = false;
            _lastItemCount = -1;
            _obstaclesSeen = 0;
            _nextEmp = _nextFurn = _nextRescan = 0f;
        }

        public string Status =>
            "employees=" + (Options.DisableEmployeeCollision.Value ? 1 : 0) + " furniture=" + (Options.DisableFurnitureCollision.Value ? 1 : 0)
            + " triggerEmployees=" + _changedColliders.Count + " agents=" + _agents.Count + " avoidanceOff=" + AvoidanceOffCount()
            + " obstaclesOff=" + _disabled.Count
            + " obstaclesSeen=" + _obstaclesSeen;

        public void Tick(float dt)
        {
#if DEBUG
            DevTrackHips();
#endif
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
                if (_changedColliders.ContainsKey(id))
                {
                    // Vanilla turns the flag back on, for example when combat ends (CombatBehaviour.End).
                    if (_movement.TryGetValue(id, out var held) && held != null && held._obstacleAvoidanceEnabled)
                        held.SetObstacleAvoidanceEnabled(false);
                    continue;
                }
                // Only the root capsule of NPCMovement. The ragdoll bones under the avatar carry their own colliders and
                // Rigidbodies; as triggers a knocked-down employee falls through the floor and never gets up.
                var changed = new List<UnityEngine.Collider>(1);
                var movement = e.Movement;
                UnityEngine.CapsuleCollider capsule = movement != null ? movement._capsuleCollider : null;
                if (capsule == null) Core.WarnOnce("collision:nocapsule:" + id, Rel.Name(e) + " has no movement capsule; its collision stays vanilla.");
                else if (!capsule.isTrigger) { capsule.isTrigger = true; changed.Add(capsule); }
                _changedColliders[id] = changed;

                var agent = e.GetComponent<NavMeshAgent>();
                if (agent == null) { Core.WarnOnce("collision:noagent:" + id, Rel.Name(e) + " has no NavMeshAgent; only its capsule was changed."); continue; }
                _agents[id] = new AgentOriginal
                {
                    Radius = agent.radius, Priority = agent.avoidancePriority, Avoidance = agent.obstacleAvoidanceType,
                    AvoidanceFlag = movement == null || movement._obstacleAvoidanceEnabled,
                };
                _agentOwner[id] = agent;
                if (agent.radius > AgentRadius) agent.radius = AgentRadius;
                agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
                agent.avoidancePriority = 0;
                if (movement == null) { Core.WarnOnce("collision:nomovement:" + id, Rel.Name(e) + " has no NPCMovement; obstacle avoidance comes back when a player is near."); continue; }
                _movement[id] = movement;
                movement.SetObstacleAvoidanceEnabled(false);
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
                // Last: with the flag back on, vanilla picks the avoidance type from the player distance again.
                if (_movement.TryGetValue(kv.Key, out var movement) && movement != null)
                    movement.SetObstacleAvoidanceEnabled(kv.Value.AvoidanceFlag);
            }
            Core.Log?.Msg("[collision] employee collision restored on " + _changedColliders.Count + " employee(s), " + restored + " collider(s).");
            _changedColliders.Clear();
            _agents.Clear();
            _agentOwner.Clear();
            _movement.Clear();
        }

        private int AvoidanceOffCount()
        {
            int n = 0;
            foreach (var kv in _movement)
                if (kv.Value != null && !kv.Value._obstacleAvoidanceEnabled) n++;
            return n;
        }

        private void FurniturePassNow(bool on, float now)
        {
            if (!on)
            {
                int back = 0;
                for (int i = 0; i < _disabled.Count; i++)
                    if (_disabled[i] != null) { _disabled[i].enabled = true; back++; }
                Core.Log?.Msg("[collision] furniture obstacles restored on " + back + " obstacle(s).");
                if (back > 0) ObstaclesChanged("furniture obstacles are back");
                _disabled.Clear();
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
            int before = _disabled.Count;
            ScanSources();
            if (_disabled.Count != before)
            {
                Core.Log?.Msg("[collision] " + (_disabled.Count - before) + " more obstacle(s) switched off (" + _disabled.Count + " in total, " + _obstaclesSeen + " found).");
                ObstaclesChanged("furniture obstacles were switched off");
            }
        }

        // Both caches hold answers from before the change: the game's NavMesh sample cache keeps snapped positions until
        // the next load (NavMeshUtility.SamplePosition), and the reachability cache keeps results for some seconds.
        private static void ObstaclesChanged(string why)
        {
            int samples = Il2CppScheduleOne.DevUtilities.NavMeshUtility.sampleCacheKeys?.Count ?? 0;
            Il2CppScheduleOne.DevUtilities.NavMeshUtility.ClearCache();
            Core.Log?.Msg("[collision] " + samples + " cached NavMesh sample(s) dropped: " + why + ".");
            Fix6ReachCache.Forget(why);
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
                    if (!o.enabled) continue;   // already off, for example on a build preview; left as it is
                    o.enabled = false;
                    _disabled.Add(o);
                }
            }
        }

#if DEBUG
        // The employee "awe collision ragdoll" knocked down, and where it stood, for "awe collision probe".
        private static Employee _ragdolled;
        private static UnityEngine.Vector3 _ragdollStart;
        private static float _minHipY = float.MaxValue;

        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("collision")) return;
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "ragdoll") { DevRagdoll(); return; }
            if (sub == "probe") { DevProbe(); return; }
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

        /// <summary>Knocks the first employee down through the vanilla ragdoll path, like a punch with a small push.</summary>
        private static void DevRagdoll()
        {
            Employee target = null;
            foreach (Employee e in Rel.Employees()) { if (e != null && e.Movement != null && e.Avatar != null && !e.Avatar.Ragdolled) { target = e; break; } }
            if (target == null) { Core.Log?.Warning("[Dev] [Collision] ragdoll: no standing employee found."); return; }
            _ragdolled = target;
            _ragdollStart = target.transform.position;
            _minHipY = float.MaxValue;
            UnityEngine.Vector3 dir = target.transform.forward;
            target.Movement.ActivateRagdoll_Server(_ragdollStart + UnityEngine.Vector3.up, dir, 5f);
            Core.Log?.Msg("[Dev] [Collision] ragdoll " + Rel.Name(target) + " at y=" + _ragdollStart.y.ToString("0.00")
                + " capsuleTrigger=" + (target.Movement._capsuleCollider != null && target.Movement._capsuleCollider.isTrigger));
        }

        /// <summary>Lowest hip height of the knocked-down employee while it lies, sampled every frame. The avatar root does not
        /// move with the ragdoll; only the bones do, so the hip bone is what falls through a floor.</summary>
        private static void DevTrackHips()
        {
            if (_ragdolled == null) return;
            var avatar = _ragdolled.Avatar;
            if (avatar == null || !avatar.Ragdolled || avatar.HipBone == null) return;
            float y = avatar.HipBone.position.y;
            if (y < _minHipY) _minHipY = y;
        }

        /// <summary>Where the knocked-down employee is now. Lying: the hip bone. Standing: the avatar, which vanilla realigns
        /// to the hips when it gets up. minHipDy is the lowest hip height since the knock, against where it stood.</summary>
        private static void DevProbe()
        {
            if (_ragdolled == null) { Core.Log?.Warning("[Dev] [Collision] probe: run 'awe collision ragdoll' first."); return; }
            var avatar = _ragdolled.Avatar;
            if (avatar == null) { Core.Log?.Warning("[Dev] [Collision] probe: " + Rel.Name(_ragdolled) + " has no avatar."); return; }
            bool down = avatar.Ragdolled;
            UnityEngine.Vector3 p = down && avatar.HipBone != null ? avatar.HipBone.position : avatar.transform.position;
            string minHip = _minHipY == float.MaxValue ? "none" : (_minHipY - _ragdollStart.y).ToString("0.00");
            Core.Log?.Msg("[Dev] [Collision] probe " + Rel.Name(_ragdolled) + " ragdolled=" + down + " y=" + p.y.ToString("0.00")
                + " startY=" + _ragdollStart.y.ToString("0.00") + " dy=" + (p.y - _ragdollStart.y).ToString("0.00")
                + " minHipDy=" + minHip);
        }
#endif
    }
}
