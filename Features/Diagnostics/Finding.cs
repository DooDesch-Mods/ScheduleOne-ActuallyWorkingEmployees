using System;

namespace ActuallyWorkingEmployees.Features.Diagnostics
{
    internal enum Sev { Info = 0, Warning = 1, Problem = 2 }

    /// <summary>One line of the Setup Doctor report or one employee badge.</summary>
    internal sealed class Finding
    {
        internal Sev Severity;
        internal string Category = "";
        internal string Subject = "";
        internal string Message = "";
        internal string Fix = "";
        /// <summary>How many identical findings were folded into this one.</summary>
        internal int Count = 1;

        internal static string SevText(Sev s)
        {
            switch (s)
            {
                case Sev.Problem: return "problem";
                case Sev.Warning: return "warning";
                default: return "info";
            }
        }
    }

    /// <summary>Severity of the game's own "cannot work" texts, shared by the badges and the Setup Doctor.</summary>
    internal static class WorkIssueInfo
    {
        /// <summary>Employee.TicksSinceLastWork at which the game itself starts to report why an employee does not work.</summary>
        internal const int IdleTicks = 5;

        internal static Sev Classify(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return Sev.Info;
            string t = reason.ToLowerInvariant();
            if (t.Contains("nothing for me to do")) return Sev.Info;
            if (t.Contains("haven't been paid")) return Sev.Info;
            if (t.Contains("waiting for a full load")) return Sev.Info;
            // Before "destination is full": the botanist's harvest text names both ("no destination or it's destination
            // is full"), and a missing destination needs the player.
            if (t.Contains("no destination")) return Sev.Warning;
            if (t.Contains("destination is full")) return Sev.Info;
            if (t.Contains("shift ends")) return Sev.Info;
            if (t.Contains("still cooking") || t.Contains("is in use")) return Sev.Info;
            if (t.Contains("haven't been assigned") || t.Contains("locker")) return Sev.Problem;
            return Sev.Warning;
        }

        /// <summary>The game's most important current reason for this employee, or null when it lists none.</summary>
        internal static bool TryFirstIssue(Employee e, out string reason, out string fix)
        {
            reason = null; fix = null;
            var issues = e.WorkIssues;
            if (issues == null || issues.Count == 0) return false;
            var first = issues[0];
            if (first == null) return false;
            reason = first.Reason;
            fix = first.Fix;
            return !string.IsNullOrEmpty(reason);
        }

        internal static string RoleOf(Employee e)
        {
            if (e.TryCast<Botanist>() != null) return "Botanist";
            if (e.TryCast<Chemist>() != null) return "Chemist";
            if (e.TryCast<Packager>() != null) return "Packager";
            if (e.TryCast<Cleaner>() != null) return "Cleaner";
            return "Employee";
        }
    }
}
