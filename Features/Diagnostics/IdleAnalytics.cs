using System;
using System.Collections.Generic;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Networking;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.Diagnostics
{
    /// <summary>
    /// Idle time and idle reason per employee. Every <c>AnalyticsIntervalSeconds</c> the host looks once at each
    /// employee: when the game has reported no work for <see cref="WorkIssueInfo.IdleTicks"/> ticks the sample counts as
    /// idle and the first work issue the game lists is counted as the reason. That text already is the clearer one
    /// from the idle-reason fix when it applies, because the fix rewrites it before the game stores it.
    /// </summary>
    internal sealed class IdleAnalytics : IFeature
    {
        internal const string FeatureName = "idle-analytics";
        public string Name => FeatureName;

        internal sealed class Stat
        {
            internal string Name = "?";
            internal string Role = "Employee";
            internal int Samples, IdleSamples;
            internal readonly Dictionary<string, int> Reasons = new Dictionary<string, int>();
        }

        private const int MaxTracked = 256;
        private const int MaxReasonsPerEmployee = 8;
        private const string NoReason = "No reason given.";
        private const string OtherReasons = "Other reasons.";

        internal static readonly Dictionary<int, Stat> Stats = new Dictionary<int, Stat>();
        internal static int TotalSamples, TotalIdle;
        private static float _acc;
        private static bool _capWarned;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("analytics", "awe analytics [reset]  (idle time and reason of every employee)", Dev);
#endif
        }

        public void Reset() { Clear(); }

        public string Status =>
            "on=" + (Options.EnableAnalytics.Value ? 1 : 0) + " interval=" + Options.AnalyticsIntervalSeconds.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
            + " tracked=" + Stats.Count + " samples=" + TotalSamples + " idle=" + TotalIdle;

        internal static void Clear()
        {
            Stats.Clear();
            TotalSamples = TotalIdle = 0;
            _acc = 0f;
            _capWarned = false;
        }

        public void Tick(float dt)
        {
            if (!Enabled()) return;
            _acc += dt;
            if (_acc < Options.AnalyticsIntervalSeconds.Value) return;
            _acc = 0f;
            Sample();
        }

        private static bool Enabled()
        {
            return Reliability.Rel.Gate(FeatureName, Options.EnableAnalytics.Value);
        }

        private static void Sample()
        {
            EmployeeManager mgr = NetworkSingleton<EmployeeManager>.Instance;
            if (mgr == null) { Reliability.Rel.MsgOnce("analytics:nomgr", "[" + FeatureName + "] EmployeeManager does not exist yet."); return; }
            var all = mgr.AllEmployees;
            if (all == null) { Reliability.Rel.MsgOnce("analytics:nolist", "[" + FeatureName + "] EmployeeManager.AllEmployees is null."); return; }
            for (int i = 0; i < all.Count; i++)
            {
                Employee e = all[i];
                if (e == null || e.Fired) continue;
                int id = e.GetInstanceID();
                if (!Stats.TryGetValue(id, out Stat s))
                {
                    if (Stats.Count >= MaxTracked)
                    {
                        if (!_capWarned) { _capWarned = true; Core.Log?.Warning("[Analytics] tracking " + MaxTracked + " employees, no more are added."); }
                        continue;
                    }
                    s = new Stat { Name = e.FullName ?? "?", Role = WorkIssueInfo.RoleOf(e) };
                    Stats[id] = s;
                    Core.Log?.Msg("[Analytics] tracking " + s.Name + " (" + s.Role + ").");
                }
                s.Samples++;
                TotalSamples++;
                if (e.TicksSinceLastWork < WorkIssueInfo.IdleTicks) continue;
                s.IdleSamples++;
                TotalIdle++;
                string reason = WorkIssueInfo.TryFirstIssue(e, out string r, out _) ? r : NoReason;
                if (!s.Reasons.ContainsKey(reason) && s.Reasons.Count >= MaxReasonsPerEmployee) reason = OtherReasons;
                s.Reasons.TryGetValue(reason, out int n);
                s.Reasons[reason] = n + 1;
            }
        }

#if DEBUG
        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("analytics")) return;
            if (args.Length > 0 && args[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                Clear();
                Core.Log?.Msg("[Dev] [Analytics] cleared.");
            }
            foreach (var kv in Stats)
            {
                Stat s = kv.Value;
                int pct = s.Samples > 0 ? (int)Math.Round(100.0 * s.IdleSamples / s.Samples) : 0;
                Core.Log?.Msg("[Dev] [Analytics] " + s.Name + " (" + s.Role + "): idle " + pct + "% of " + s.Samples + " sample(s), " + s.Reasons.Count + " reason(s)");
            }
            Core.Log?.Msg("[Dev] [Analytics] " + new IdleAnalytics().Status);
        }
#endif
    }
}
