using System.Collections.Generic;

namespace ActuallyWorkingEmployees.Features.Diagnostics
{
    /// <summary>
    /// One problem badge per employee, built on request for the phone app. Nothing runs between requests. The text is
    /// the game's own first work issue (the idle-reason fix has already rewritten it where it can), or the missing
    /// bed or locker when the game lists nothing yet.
    /// </summary>
    internal static class ProblemBadges
    {
        internal sealed class Badge
        {
            internal string Name, Role, Text;
            internal Sev Severity;
            /// <summary>False for an employee that works or has no reason to report.</summary>
            internal bool HasProblem;
        }

        internal static List<Badge> Build()
        {
            var list = new List<Badge>();
            EmployeeManager mgr = NetworkSingleton<EmployeeManager>.Instance;
            if (mgr == null) { Reliability.Rel.MsgOnce("badges:nomgr", "[Badges] EmployeeManager does not exist yet."); return list; }
            var all = mgr.AllEmployees;
            if (all == null) { Reliability.Rel.MsgOnce("badges:nolist", "[Badges] EmployeeManager.AllEmployees is null."); return list; }
            for (int i = 0; i < all.Count; i++)
            {
                Employee e = all[i];
                if (e == null || e.Fired) continue;
                var b = new Badge { Name = e.FullName ?? "?", Role = WorkIssueInfo.RoleOf(e), Text = "Working.", Severity = Sev.Info };
                if (e.GetHome() == null)
                {
                    b.HasProblem = true; b.Severity = Sev.Problem; b.Text = "No bed or locker assigned.";
                }
                else if (e.TicksSinceLastWork >= WorkIssueInfo.IdleTicks && WorkIssueInfo.TryFirstIssue(e, out string reason, out _))
                {
                    b.Severity = WorkIssueInfo.Classify(reason);
                    b.HasProblem = b.Severity != Sev.Info;
                    b.Text = reason;
                }
                list.Add(b);
            }
            list.Sort((x, y) => ((int)y.Severity).CompareTo((int)x.Severity));
            return list;
        }
    }
}
