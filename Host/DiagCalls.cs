using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features.Diagnostics;
using ActuallyWorkingEmployees.Networking;

namespace ActuallyWorkingEmployees.Host
{
    /// <summary>
    /// The diagnostics half of the bridge: badges, Setup Doctor and idle analytics for the Diagnostics page, and the
    /// two hints (host-controlled notice, station speed warning) that depend on option values. Everything is computed
    /// on the host when the page asks; a client only gets "host" = false.
    /// </summary>
    internal static class DiagCalls
    {
        internal const string LockNotice = "Some settings are set by the host.";
        internal const string StationHint = "Saved mixes and stations that already run keep their old time.";
        private const int MaxAnalyticsRows = 30;
        private const int MaxReasonsPerRow = 3;

        /// <summary>Hints keyed by group name, shown above the options of that group.</summary>
        internal static JObject Hints()
        {
            var h = new JObject();
            if (Options.ShowStationSpeedWarning.Value && StationSpeedChanged()) h["Stations"] = StationHint;
            return h;
        }

        /// <summary>The text of the host-controlled band on a client, or empty.</summary>
        internal static string Notice()
        {
            return Net.IsClientOnly && Options.ShowCoopLockNotice.Value ? LockNotice : "";
        }

        private static bool StationSpeedChanged()
        {
            foreach (Option o in Options.InGroup("Stations"))
                if (o.Balance && !o.IsDefault) return true;
            return false;
        }

        internal static JObject Diag(JObject _)
        {
            return Build();
        }

        internal static JObject DoctorRun(JObject _)
        {
            if (!SetupDoctor.Run(out string refusal)) return Calls.Fail(refusal);
            JObject j = Build();
            j["ran"] = true;
            return j;
        }

        internal static JObject AnalyticsReset(JObject _)
        {
            if (!Net.IsServer) { Core.Log?.Warning("[App] analytics reset refused: host only."); return Calls.Fail("Host only."); }
            IdleAnalytics.Clear();
            Core.Log?.Msg("[App] analytics cleared.");
            return Build();
        }

        internal static JObject Build()
        {
            bool host = Net.IsServer;
            return new JObject
            {
                ["ok"] = true,
                ["host"] = host,
                ["badges"] = BadgesJson(host),
                ["doctor"] = DoctorJson(host),
                ["analytics"] = AnalyticsJson(host),
            };
        }

        private static JObject BadgesJson(bool host)
        {
            bool on = Options.ShowProblemBadges.Value;
            var items = new JArray();
            int problems = 0;
            if (host && on)
            {
                foreach (ProblemBadges.Badge b in ProblemBadges.Build())
                {
                    if (b.HasProblem) problems++;
                    items.Add(new JObject
                    {
                        ["name"] = b.Name, ["role"] = b.Role, ["sev"] = b.HasProblem ? Finding.SevText(b.Severity) : "ok",
                        ["text"] = b.Text,
                    });
                }
            }
            return new JObject { ["enabled"] = on, ["problems"] = problems, ["items"] = items };
        }

        private static JObject DoctorJson(bool host)
        {
            var items = new JArray();
            bool ran = host && SetupDoctor.HasRun;
            if (ran)
            {
                for (int i = 0; i < SetupDoctor.Shown; i++)
                {
                    Finding f = SetupDoctor.Findings[i];
                    items.Add(new JObject
                    {
                        ["sev"] = Finding.SevText(f.Severity), ["cat"] = f.Category, ["subject"] = f.Subject,
                        ["msg"] = f.Message, ["fix"] = f.Fix, ["count"] = f.Count,
                    });
                }
            }
            return new JObject
            {
                ["enabled"] = Options.EnableSetupDoctor.Value,
                ["ran"] = ran,
                ["total"] = ran ? SetupDoctor.Total : 0,
                ["shown"] = ran ? SetupDoctor.Shown : 0,
                ["summary"] = ran ? SetupDoctor.Summary() : "",
                ["ageSec"] = ran ? (int)(UnityEngine.Time.realtimeSinceStartup - SetupDoctor.RanAt) : 0,
                ["findings"] = items,
            };
        }

        private static JObject AnalyticsJson(bool host)
        {
            var items = new JArray();
            if (host && Options.EnableAnalytics.Value)
            {
                var rows = IdleAnalytics.Stats.Values.OrderByDescending(s => s.Samples == 0 ? 0.0 : (double)s.IdleSamples / s.Samples).Take(MaxAnalyticsRows);
                foreach (IdleAnalytics.Stat s in rows)
                {
                    var reasons = new JArray();
                    foreach (var kv in s.Reasons.OrderByDescending(r => r.Value).Take(MaxReasonsPerRow))
                        reasons.Add(new JObject { ["text"] = kv.Key, ["count"] = kv.Value });
                    items.Add(new JObject
                    {
                        ["name"] = s.Name, ["role"] = s.Role, ["samples"] = s.Samples, ["idle"] = s.IdleSamples,
                        ["idlePct"] = s.Samples == 0 ? 0 : (int)Math.Round(100.0 * s.IdleSamples / s.Samples),
                        ["reasons"] = reasons,
                    });
                }
            }
            return new JObject
            {
                ["enabled"] = Options.EnableAnalytics.Value,
                ["interval"] = Math.Round(Options.AnalyticsIntervalSeconds.Value, 1),
                ["samples"] = IdleAnalytics.TotalSamples,
                ["items"] = items,
            };
        }
    }
}
