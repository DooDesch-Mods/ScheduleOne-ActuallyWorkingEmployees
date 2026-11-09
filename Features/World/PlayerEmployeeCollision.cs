using System;
using System.Collections.Generic;
using UnityEngine;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features.Reliability;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.World
{
    /// <summary>
    /// Players walk through employees (PlayersPassThroughEmployees). The option is set by the host and synced, and every
    /// game applies it to its own local player: player movement is simulated where the player sits, so a client's player
    /// is blocked by the employee colliders on that client, not on the host.
    ///
    /// Physics.IgnoreCollision is set between the local player's CharacterController and body capsule on one side and every
    /// collider of an employee (its NPCMovement capsule and the avatar's bone colliders) on the other. Employees keep
    /// colliding with each other, with the world and with other NPCs; raycasts (punches, interaction) are not affected.
    /// Vanilla sets ignore pairs between the player and vehicle colliders only, so turning the option off again resets
    /// exactly the pairs this feature set.
    /// </summary>
    internal sealed class PlayerEmployeeCollision : IFeature
    {
        internal const string FeatureName = "player-collision";
        public string Name => FeatureName;

        private const float Pass = 1f;

        // Per employee instance id: the employee colliders that are ignored against the player colliders in _playerCols.
        private readonly Dictionary<int, List<Collider>> _ignored = new Dictionary<int, List<Collider>>();
        private readonly List<Collider> _playerCols = new List<Collider>(2);
        private int _playerId;
        private bool _was;
        private float _next;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("playerpass", "awe playerpass [walk]  (pairs set for the local player; walk moves the player into the nearest employee and reports whether it got through)", Dev);
#endif
        }

        public void Reset()
        {
            _ignored.Clear();
            _playerCols.Clear();
            _playerId = 0;
            _was = false;
            _next = 0f;
        }

        /// <summary>
        /// The host's switch. A co-op client keeps host values for display only (Option.SetHostValue), so it reads the
        /// synced text: "1"/"true" is on, and no value (a host without this mod) is vanilla.
        /// </summary>
        private static bool On
        {
            get
            {
                if (!ActuallyWorkingEmployees.Networking.Net.IsClientOnly) return Options.PlayersPassThroughEmployees.Value;
                string raw = Options.PlayersPassThroughEmployees.HostValueText;
                return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        public string Status => "on=" + (On ? 1 : 0) + " employees=" + _ignored.Count
            + " playerColliders=" + _playerCols.Count;

        public void Tick(float dt)
        {
            bool on = On;
            if (!on && !_was) return;
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + Pass;
            PassNow(on);
        }

        private void PassNow(bool on)
        {
            if (!on) { Restore("the option is off"); _was = false; return; }

            var player = Il2CppScheduleOne.PlayerScripts.Player.Local;
            if (player == null) { Rel.MsgOnce("player-collision:noplayer", "[player-collision] no local player yet; waiting."); return; }
            if (player.GetInstanceID() != _playerId)
            {
                if (_playerId != 0) Restore("the local player changed");
                if (!CollectPlayer(player)) return;
                _playerId = player.GetInstanceID();
            }
            if (!_was) Core.Log?.Msg("[player-collision] players pass through employees (" + _playerCols.Count + " player collider(s)).");
            _was = true;

            int added = 0;
            foreach (Employee e in Rel.Employees())
            {
                if (e == null) continue;
                int id = e.GetInstanceID();
                if (_ignored.ContainsKey(id)) continue;
                var cols = new List<Collider>();
                var movement = e.Movement;
                if (movement != null && movement._capsuleCollider != null) cols.Add(movement._capsuleCollider);
                var bones = e.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < bones.Length; i++)
                    if (bones[i] != null && !cols.Contains(bones[i])) cols.Add(bones[i]);
                if (cols.Count == 0) { Core.WarnOnce("player-collision:nocols:" + id, Rel.Name(e) + " has no collider; nothing to ignore."); continue; }
                SetPairs(cols, true);
                _ignored[id] = cols;
                added++;
            }
            if (added > 0) Core.Log?.Msg("[player-collision] " + added + " more employee(s) ignored by the local player (" + _ignored.Count + " in total).");
        }

        private bool CollectPlayer(Il2CppScheduleOne.PlayerScripts.Player player)
        {
            _playerCols.Clear();
            var movement = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
            if (movement != null && movement.Controller != null) _playerCols.Add(movement.Controller);
            else Core.WarnOnce("player-collision:nocontroller", "[player-collision] the local player has no CharacterController yet.");
            if (player.CapCol != null) _playerCols.Add(player.CapCol);
            if (_playerCols.Count == 0) { Core.WarnOnce("player-collision:nocols", "[player-collision] the local player has no collider; nothing to ignore."); return false; }
            return true;
        }

        private void SetPairs(List<Collider> employeeCols, bool ignore)
        {
            for (int p = 0; p < _playerCols.Count; p++)
            {
                Collider pc = _playerCols[p];
                if (pc == null) continue;
                for (int i = 0; i < employeeCols.Count; i++)
                    if (employeeCols[i] != null) Physics.IgnoreCollision(pc, employeeCols[i], ignore);
            }
        }

        private void Restore(string why)
        {
            if (_ignored.Count == 0 && _playerCols.Count == 0) return;
            foreach (var kv in _ignored) SetPairs(kv.Value, false);
            Core.Log?.Msg("[player-collision] collision with " + _ignored.Count + " employee(s) restored: " + why + ".");
            _ignored.Clear();
            _playerCols.Clear();
            _playerId = 0;
        }

#if DEBUG
        private static void Dev(string[] args)
        {
            PlayerEmployeeCollision self = null;
            foreach (IFeature f in FeatureHost.All) if (f is PlayerEmployeeCollision c) { self = c; break; }
            if (self == null) { Core.Log?.Warning("[Dev] [PlayerPass] the feature is not registered."); return; }
            self.PassNow(On);
            self._next = Time.unscaledTime + Pass;
            Core.Log?.Msg("[Dev] [PlayerPass] " + self.Status);
            if (args.Length > 0 && string.Equals(args[0], "walk", StringComparison.OrdinalIgnoreCase)) DevWalk();
        }

        /// <summary>
        /// Puts the local player 1.5 m in front of the nearest standing employee and moves its CharacterController 3 m
        /// straight through the employee's position in small steps, all in one frame, the way PlayerMovement.Move does.
        /// "through" = the player ended more than 1 m past the employee, or past it and stopped by something that is no employee.
        /// </summary>
        private static void DevWalk()
        {
            var player = Il2CppScheduleOne.PlayerScripts.Player.Local;
            var movement = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
            if (player == null || movement == null || movement.Controller == null) { Core.Log?.Warning("[Dev] [PlayerPass] walk: no local player movement."); return; }
            Employee target = null;
            float best = float.MaxValue;
            foreach (Employee e in Rel.Employees())
            {
                if (e == null || e.Movement == null || e.Movement._capsuleCollider == null || (e.Avatar != null && e.Avatar.Ragdolled)) continue;
                float d = (e.transform.position - player.transform.position).sqrMagnitude;
                if (d < best) { best = d; target = e; }
            }
            if (target == null) { Core.Log?.Warning("[Dev] [PlayerPass] walk: no standing employee."); return; }
            CharacterController cc = movement.Controller;
            Vector3 emp = target.transform.position;
            Vector3 dir = target.transform.forward; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            dir.Normalize();
            Vector3 start = emp + dir * 1.5f + Vector3.up * (cc.height / 2f + 0.05f);
            cc.enabled = false;
            cc.transform.position = start;
            cc.enabled = true;
            Physics.SyncTransforms();
            for (int i = 0; i < 30; i++) cc.Move(-dir * 0.1f);
            Vector3 end = cc.transform.position;
            float past = Vector3.Dot(end - emp, -dir);
            bool trigger = target.Movement._capsuleCollider.isTrigger;
            // What stopped the walk early: an employee collider means the pair was not ignored, anything else (a wall or
            // furniture behind the employee) means the player got through the employee and hit the room.
            string blocker = "none";
            bool blockerIsEmployee = false;
            if (past < 1f)
            {
                // Rays from inside the player's capsule skip its own colliders. Physics.CapsuleCastAll is stripped from
                // the IL2CPP build, Physics.Raycast is used by the game itself.
                Vector3 c = end + cc.center;
                float half = Mathf.Max(0f, cc.height / 2f - cc.radius);
                float nearest = float.MaxValue;
                for (int k = -1; k <= 1; k++)
                {
                    if (!Physics.Raycast(c + Vector3.up * (half * k), -dir, out RaycastHit h, cc.radius + 0.3f, ~0, QueryTriggerInteraction.Ignore)) continue;
                    if (h.collider == null || h.distance >= nearest || h.collider.transform.IsChildOf(player.transform)) continue;
                    nearest = h.distance;
                    blocker = h.collider.name;
                    blockerIsEmployee = h.collider.GetComponentInParent<Employee>() != null;
                }
            }
            bool through = past > 1f || (past > 0.3f && blocker != "none" && !blockerIsEmployee);
            Core.Log?.Msg("[Dev] [PlayerPass] walk " + Rel.Name(target) + ": moved " + (end - start).magnitude.ToString("0.00") + " m, past=" + past.ToString("0.00")
                + " blocker=" + blocker + " blockerEmployee=" + blockerIsEmployee
                + " through=" + through + " option=" + On + " capsuleTrigger=" + trigger);
        }
#endif
    }
}
