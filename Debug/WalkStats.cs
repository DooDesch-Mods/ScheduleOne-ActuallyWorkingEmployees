#if DEBUG
using System;
using System.Collections.Generic;
using ActuallyWorkingEmployees.Features;
using ActuallyWorkingEmployees.Features.Reliability;
using NpcBehaviour = Il2CppScheduleOne.NPCs.Behaviour.Behaviour;

namespace ActuallyWorkingEmployees.Debugging
{
    /// <summary>
    /// DEBUG-only walk counter per employee role, to find the highest walk speed that still paths cleanly. Every
    /// WalkCallback is counted by result. A successful walk is followed for up to 2 s while the employee has no new
    /// destination: the largest distance from the target in that window is the overshoot, and more than 1 m counts as
    /// an overshoot event. Every 0.25 s the ground speed of each walking employee is sampled. 'awe walkstats' prints one
    /// line per role, 'awe walkstats reset' clears the counters.
    /// </summary>
    internal sealed class WalkStats : IFeature
    {
        public string Name => "walkstats";

        private const float FollowSeconds = 2f, OvershootMeters = 1f;

        private sealed class Role
        {
            internal int Success, Partial, Failed, Interrupted, Stopped, Overshoots, Followed;
            internal double ArriveSum, OverSum;
            internal float ArriveMax, OverMax;
            internal int SpeedSamples;
            internal double SpeedSum;
            internal float SpeedMax;
        }

        private static readonly List<Employee> _employees = new List<Employee>();
        private float _nextSpeed;

        private sealed class Follow { internal Employee Emp; internal string Role; internal Vector3 Dest; internal float Until; internal float Max; }

        private static readonly Dictionary<string, Role> _roles = new Dictionary<string, Role>(StringComparer.Ordinal);
        private static readonly List<Follow> _follow = new List<Follow>();
        private static float _since;

        public void Init()
        {
            DevConsole.Register("walkstats", "awe walkstats [reset]  (walk results and overshoot per employee role)", Dev);
            _since = 0f;
        }

        public void Reset()
        {
            _follow.Clear();
        }

        public string Status
        {
            get
            {
                int s = 0, p = 0, f = 0, o = 0;
                foreach (Role r in _roles.Values) { s += r.Success; p += r.Partial; f += r.Failed; o += r.Overshoots; }
                return "success=" + s + " partial=" + p + " failed=" + f + " overshoots=" + o;
            }
        }

        public void Tick(float dt)
        {
            if (Time.time >= _nextSpeed)
            {
                _nextSpeed = Time.time + 0.25f;
                SampleSpeeds();
            }
            for (int i = _follow.Count - 1; i >= 0; i--)
            {
                Follow f = _follow[i];
                bool done = Time.time > f.Until;
                try
                {
                    if (f.Emp == null || f.Emp.Movement == null) done = true;
                    // A new walk started: the employee is heading elsewhere, so the distance no longer says anything.
                    else if (f.Emp.Movement._hasDestination) done = true;
                    else
                    {
                        float d = Vector3.Distance(f.Emp.Movement.FootPosition, f.Dest);
                        if (d > f.Max) f.Max = d;
                    }
                }
                catch (Exception e) { Core.Caught("walkstats: follow " + f.Role, e); done = true; }
                if (!done) continue;
                Role r = Get(f.Role);
                r.Followed++;
                r.OverSum += f.Max;
                if (f.Max > r.OverMax) r.OverMax = f.Max;
                if (f.Max > OvershootMeters)
                {
                    r.Overshoots++;
                    if (r.Overshoots == 1 || r.Overshoots % 20 == 0)
                        Core.Log?.Msg("[WalkStats] overshoot " + f.Role + " " + Rel.Name(f.Emp) + ": " + f.Max.ToString("F2") + " m past its target (#" + r.Overshoots + ").");
                }
                _follow.RemoveAt(i);
            }
        }

        /// <summary>Ground speed of every walking employee's NavMeshAgent, so a walk speed setting is seen to take effect.</summary>
        private static void SampleSpeeds()
        {
            Rel.EmployeesInto(_employees);
            for (int i = 0; i < _employees.Count; i++)
            {
                Employee emp = _employees[i];
                try
                {
                    if (emp.Movement == null || !emp.Movement.IsMoving || emp.Movement._agent == null) continue;
                    Vector3 v = emp.Movement._agent.velocity;
                    float speed = new Vector3(v.x, 0f, v.z).magnitude;
                    Role r = Get(emp.Type.ToString());
                    r.SpeedSamples++;
                    r.SpeedSum += speed;
                    if (speed > r.SpeedMax) r.SpeedMax = speed;
                }
                catch (Exception e) { Core.Caught("walkstats: speed " + Rel.Name(emp), e); }
            }
        }

        private static Role Get(string role)
        {
            if (!_roles.TryGetValue(role, out Role r)) { r = new Role(); _roles[role] = r; }
            return r;
        }

        /// <summary>Called from the WalkCallback postfix before the vanilla code clears the destination.</summary>
        internal static void Record(NpcBehaviour b, NPCMovement.WalkResult result)
        {
            NPC npc = b != null ? b.Npc : null;
            Employee emp = npc != null ? npc.TryCast<Employee>() : null;
            if (emp == null) return;   // customers and dealers are not measured
            if (_since == 0f) _since = Time.time;
            string role = emp.Type.ToString();
            Role r = Get(role);
            switch (result)
            {
                case NPCMovement.WalkResult.Success:
                    r.Success++;
                    Vector3 dest = emp.Movement.CurrentDestination;
                    if (dest == Vector3.zero) return;
                    float arrive = Vector3.Distance(emp.Movement.FootPosition, dest);
                    r.ArriveSum += arrive;
                    if (arrive > r.ArriveMax) r.ArriveMax = arrive;
                    _follow.Add(new Follow { Emp = emp, Role = role, Dest = dest, Until = Time.time + FollowSeconds, Max = arrive });
                    break;
                case NPCMovement.WalkResult.Partial: r.Partial++; break;
                case NPCMovement.WalkResult.Failed: r.Failed++; break;
                case NPCMovement.WalkResult.Interrupted: r.Interrupted++; break;
                case NPCMovement.WalkResult.Stopped: r.Stopped++; break;
            }
        }

        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("walkstats")) return;
            if (args.Length > 0 && args[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                _roles.Clear();
                _follow.Clear();
                _since = Time.time;
                Core.Log?.Msg("[Dev] awe walkstats reset: counters cleared.");
                return;
            }
            float span = _since > 0f ? Time.time - _since : 0f;
            if (_roles.Count == 0) Core.Log?.Msg("[Dev] [WalkStats] no employee walk ended yet.");
            foreach (KeyValuePair<string, Role> kv in _roles)
            {
                Role r = kv.Value;
                Core.Log?.Msg("[Dev] [WalkStats] " + kv.Key + ": success=" + r.Success + " partial=" + r.Partial + " failed=" + r.Failed
                    + " interrupted=" + r.Interrupted + " stopped=" + r.Stopped
                    + " arriveAvg=" + (r.Success > 0 ? (r.ArriveSum / r.Success).ToString("F2") : "-") + " arriveMax=" + r.ArriveMax.ToString("F2")
                    + " overAvg=" + (r.Followed > 0 ? (r.OverSum / r.Followed).ToString("F2") : "-") + " overMax=" + r.OverMax.ToString("F2")
                    + " overshoots=" + r.Overshoots + "/" + r.Followed
                    + " speedAvg=" + (r.SpeedSamples > 0 ? (r.SpeedSum / r.SpeedSamples).ToString("F2") : "-") + " speedMax=" + r.SpeedMax.ToString("F2"));
            }
            Core.Log?.Msg("[Dev] [WalkStats] " + new WalkStats().Status + " over " + span.ToString("F0") + " s, following " + _follow.Count + ".");
        }
    }
}
#endif
