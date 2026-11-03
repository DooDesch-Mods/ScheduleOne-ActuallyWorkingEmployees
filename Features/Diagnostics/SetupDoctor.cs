using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.ObjectScripts;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Networking;
using Prop = Il2CppScheduleOne.Property.Property;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.Diagnostics
{
    /// <summary>
    /// The Setup Doctor: one pass over the owned properties that lists concrete setup problems - employees without a
    /// bed or locker, employees without stations or routes, routes with a missing end or an unwalkable path, stations
    /// nobody serves, planted pots without a botanist or a harvest destination. Runs once on request (phone app button
    /// or <c>awe doctor</c>), never on a timer, and only on the host. The report is kept for the app.
    /// </summary>
    internal sealed class SetupDoctor : IFeature
    {
        internal const string FeatureName = "setup-doctor";
        public string Name => FeatureName;

        /// <summary>Routes path-tested per run at most, so a huge base cannot stall the frame.</summary>
        private const int MaxReachabilityRoutes = 150;

        internal static bool HasRun;
        internal static List<Finding> Findings = new List<Finding>();   // all findings, worst first
        internal static int Total, Shown, Properties, Employees, Routes, ReachTested, ElapsedMs;
        internal static bool ReachabilityOn;
        internal static float RanAt;
        private static int _runs;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("doctor", "awe doctor  (run the Setup Doctor and log its findings)", Dev);
#endif
        }

        public void Tick(float dt) { }

        public void Reset()
        {
            HasRun = false;
            Findings = new List<Finding>();
            Total = Shown = Properties = Employees = Routes = ReachTested = ElapsedMs = 0;
        }

        public string Status =>
            "on=" + (Options.EnableSetupDoctor.Value ? 1 : 0) + " runs=" + _runs
            + " findings=" + (HasRun ? Total.ToString() : "-");

        /// <summary>
        /// Runs the audit. False with <paramref name="refusal"/> (also logged) when it may not run; otherwise the result
        /// is in the static fields.
        /// </summary>
        internal static bool Run(out string refusal)
        {
            refusal = null;
            if (!Options.EnableSetupDoctor.Value)
            {
                refusal = "The Setup Doctor is off.";
                Core.Log?.Warning("[Doctor] refused: diagnostics.enablesetupdoctor is off.");
                return false;
            }
            if (!Net.IsServer)
            {
                refusal = "Host only.";
                Core.Log?.Warning("[Doctor] refused: only the host can audit the setup.");
                return false;
            }
            if (!Core.InWorld)
            {
                refusal = "No save is loaded.";
                Core.Log?.Warning("[Doctor] refused: no save is loaded.");
                return false;
            }

            var sw = Stopwatch.StartNew();
            var raw = new List<Finding>();
            int props = 0, emps = 0, routes = 0, tested = 0;
            ReachabilityOn = Options.DoctorCheckReachability.Value;

            var owned = Prop.OwnedProperties;
            if (owned == null)
            {
                refusal = "The game has no property list yet.";
                Core.Log?.Warning("[Doctor] refused: Property.OwnedProperties is null.");
                return false;
            }
            for (int p = 0; p < owned.Count; p++)
            {
                Prop prop = owned[p];
                if (prop == null) continue;
                props++;
                try { AuditProperty(prop, raw, ref emps, ref routes, ref tested); }
                catch (Exception e) { Core.Failed("doctor: audit property " + Safe(prop), e); }
            }

            Findings = Fold(raw);
            Total = Findings.Count;
            Shown = Math.Min(Total, Options.DoctorMaxFindings.Value);
            Properties = props; Employees = emps; Routes = routes; ReachTested = tested;
            ElapsedMs = (int)sw.ElapsedMilliseconds;
            RanAt = Time.realtimeSinceStartup;
            HasRun = true;
            _runs++;

            Log();
            return true;
        }

        private static void AuditProperty(Prop prop, List<Finding> f, ref int emps, ref int routes, ref int tested)
        {
            string where = Safe(prop);
            var employees = prop.Employees;
            int count = employees?.Count ?? 0;
            int cap = prop.EmployeeCapacity;
            if (cap > 0 && count > cap)
                Add(f, Sev.Warning, "Capacity", where, count + " employees on a property rated for " + cap + ".",
                    Options.ShareIdlePoints.Value ? "Extra employees share idle spots."
                        : "Extra employees get no idle spot (ShareIdlePoints is off).");

            var assigned = new HashSet<int>();
            var routeList = new List<AdvancedTransitRoute>();
            Employee handler = null;
            for (int i = 0; i < count; i++)
            {
                Employee e = employees[i];
                if (e == null || e.Fired) continue;
                emps++;
                try { AuditEmployee(e, where, f, assigned, routeList); }
                catch (Exception ex) { Core.Failed("doctor: audit employee " + EmpName(e), ex); }
                if (handler == null && IsOnMesh(e)) handler = e;
            }

            AuditStations(prop, where, f, assigned);

            for (int r = 0; r < routeList.Count; r++)
            {
                routes++;
                try { AuditRoute(routeList[r], where, handler, f, ref tested); }
                catch (Exception ex) { Core.Failed("doctor: audit route", ex); }
            }
        }

        private static void AuditEmployee(Employee e, string where, List<Finding> f, HashSet<int> assigned, List<AdvancedTransitRoute> routes)
        {
            string name = EmpName(e);
            if (e.GetHome() == null)
                Add(f, Sev.Problem, "Employee", name, "No bed or locker assigned.", "Assign a locker with the management clipboard.");

            Botanist b = e.TryCast<Botanist>();
            Chemist c = e.TryCast<Chemist>();
            Packager pk = e.TryCast<Packager>();
            Cleaner cl = e.TryCast<Cleaner>();
            if (b != null)
            {
                var cfg = b.configuration;
                if (cfg == null) { Core.WarnOnce("doctor:nocfg:" + e.GetInstanceID(), name + " has no configuration, skipped."); return; }
                int n = Ids(assigned, cfg.AssignedPots) + Ids(assigned, cfg.AssignedRacks)
                      + Ids(assigned, cfg.AssignedBeds) + Ids(assigned, cfg.AssignedSpawnStations);
                if (n == 0) Add(f, Sev.Problem, "Employee", name, "No pots, racks or beds assigned.", "Assign stations with the management clipboard.");
            }
            else if (c != null)
            {
                var cfg = c.configuration;
                if (cfg == null) { Core.WarnOnce("doctor:nocfg:" + e.GetInstanceID(), name + " has no configuration, skipped."); return; }
                int n = Ids(assigned, cfg.ChemStations) + Ids(assigned, cfg.LabOvens)
                      + Ids(assigned, cfg.Cauldrons) + Ids(assigned, cfg.MixStations);
                if (n == 0) Add(f, Sev.Problem, "Employee", name, "No stations assigned.", "Assign stations with the management clipboard.");
            }
            else if (pk != null)
            {
                var cfg = pk.configuration;
                if (cfg == null) { Core.WarnOnce("doctor:nocfg:" + e.GetInstanceID(), name + " has no configuration, skipped."); return; }
                int n = Ids(assigned, cfg.AssignedStations) + Ids(assigned, cfg.AssignedBrickPresses);
                var list = cfg.Routes?.Routes;
                int rc = list?.Count ?? 0;
                for (int i = 0; i < rc; i++) if (list[i] != null) routes.Add(list[i]);
                if (n == 0 && rc == 0) Add(f, Sev.Problem, "Employee", name, "No stations or routes assigned.", "Assign stations or add routes with the management clipboard.");
            }
            else if (cl != null)
            {
                var bins = cl.configuration?.binItems;
                if (bins == null) { Core.WarnOnce("doctor:nobins:" + e.GetInstanceID(), name + " has no bin list, skipped."); return; }
                if (bins.Count == 0) Add(f, Sev.Problem, "Employee", name, "No trash cans assigned.", "Assign trash cans with the management clipboard.");
            }

            if (e.TicksSinceLastWork >= WorkIssueInfo.IdleTicks && WorkIssueInfo.TryFirstIssue(e, out string reason, out string fix))
            {
                if (reason.ToLowerInvariant().Contains("locker")) return;   // already reported as the missing bed or locker
                Add(f, WorkIssueInfo.Classify(reason), "Employee", name, reason, fix ?? "");
            }
        }

        private static void AuditStations(Prop prop, string where, List<Finding> f, HashSet<int> assigned)
        {
            var configurables = prop.Configurables;
            if (configurables == null) return;
            for (int i = 0; i < configurables.Count; i++)
            {
                IConfigurable cfgable = configurables[i];
                if (cfgable == null) continue;
                string kind = StationName(cfgable.ConfigurableType);
                if (kind == null) continue;
                BuildableItem item = cfgable.TryCast<BuildableItem>();
                if (item == null) continue;

                bool served = assigned.Contains(item.GetInstanceID());
                Pot pot = cfgable.TryCast<Pot>();
                bool planted = pot != null && pot.Plant != null;
                if (!served)
                {
                    if (planted) Add(f, Sev.Warning, "Station", where, "A pot with a plant is not assigned to a botanist.", "Assign the pot to a botanist.");
                    else Add(f, Sev.Info, "Station", where, "A " + kind + " is not assigned to any employee.", "");
                }
                if (planted)
                {
                    var pc = pot.Configuration?.TryCast<PotConfiguration>();
                    if (pc != null && pc.Destination != null && pc.Destination.SelectedObject == null)
                        Add(f, Sev.Warning, "Station", where, "A pot with a plant has no harvest destination.", "Set a destination for the pot.");
                }
            }
        }

        private static void AuditRoute(AdvancedTransitRoute route, string where, Employee handler, List<Finding> f, ref int tested)
        {
            if (route == null) return;
            if (!route.AreEntitiesNonNull())
            {
                Add(f, Sev.Problem, "Route", where, "A route has no source or no destination.", "Something on the route was sold or removed. Create the route again.");
                return;
            }
            string src = EntityName(route.Source), dst = EntityName(route.Destination);
            string subject = src + " -> " + dst;
            if (!route.Destination.IsAcceptingItems)
            {
                Add(f, Sev.Warning, "Route", subject, "The destination is not accepting items.", "It may be switched off or busy. Check it with the management clipboard.");
                return;
            }
            if (!ReachabilityOn) return;
            if (handler == null)
            {
                Core.WarnOnce("doctor:nohandler:" + where, "no employee on the NavMesh at " + where + ", routes there were not path-tested.");
                return;
            }
            if (tested >= MaxReachabilityRoutes)
            {
                Core.WarnOnce("doctor:reachcap", "path-test limit of " + MaxReachabilityRoutes + " routes reached, the rest were not tested.");
                return;
            }
            var move = handler.MoveItemBehaviour;
            if (move == null)
            {
                Core.WarnOnce("doctor:nomove:" + handler.GetInstanceID(), EmpName(handler) + " has no MoveItemBehaviour, routes were not path-tested.");
                return;
            }
            tested++;
            bool s = move.CanGetToSource(route);
            bool d = move.CanGetToDestination(route);
            if (!(s && d))
                Add(f, Sev.Problem, "Route", subject, "No walkable path to the " + (s ? "destination" : "source") + ".",
                    "Furniture or a missing connection blocks it. Handlers skip routes they cannot walk.");
        }

        private static bool IsOnMesh(Employee e)
        {
            if (e.MoveItemBehaviour == null) return false;
            var agent = e.GetComponent<UnityEngine.AI.NavMeshAgent>();
            return agent != null && agent.isOnNavMesh;
        }

        private static string StationName(EConfigurableType t)
        {
            switch (t)
            {
                case EConfigurableType.Pot: return "pot";
                case EConfigurableType.PackagingStation: return "packaging station";
                case EConfigurableType.LabOven: return "lab oven";
                case EConfigurableType.ChemistryStation: return "chemistry station";
                case EConfigurableType.Cauldron: return "cauldron";
                case EConfigurableType.BrickPress: return "brick press";
                case EConfigurableType.MixingStation: return "mixing station";
                case EConfigurableType.DryingRack: return "drying rack";
                case EConfigurableType.SpawnStation: return "spawn station";
                case EConfigurableType.MushroomBed: return "mushroom bed";
                default: return null;
            }
        }

        /// <summary>Adds the instance ids of an employee's assigned objects to the set and returns how many there are.</summary>
        private static int Ids<T>(HashSet<int> set, Il2CppSystem.Collections.Generic.List<T> list) where T : UnityEngine.Object
        {
            if (list == null) return 0;
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                T t = list[i];
                if (t == null) continue;
                set.Add(t.GetInstanceID());
                n++;
            }
            return n;
        }

        private static void Add(List<Finding> f, Sev sev, string cat, string subject, string msg, string fix)
        {
            f.Add(new Finding { Severity = sev, Category = cat, Subject = subject, Message = msg, Fix = fix });
        }

        /// <summary>Folds identical findings into one with a count and sorts the worst first.</summary>
        private static List<Finding> Fold(List<Finding> raw)
        {
            var byKey = new Dictionary<string, Finding>();
            var order = new List<Finding>();
            foreach (Finding x in raw)
            {
                string key = (int)x.Severity + "|" + x.Category + "|" + x.Subject + "|" + x.Message;
                if (byKey.TryGetValue(key, out Finding first)) { first.Count++; continue; }
                byKey[key] = x;
                order.Add(x);
            }
            return order.OrderByDescending(x => (int)x.Severity).ToList();
        }

        private static void Log()
        {
            Core.Log?.Msg("[Doctor] " + Summary());
            int cap = Shown;
            for (int i = 0; i < cap; i++)
            {
                Finding x = Findings[i];
                string line = "[Doctor] " + Finding.SevText(x.Severity) + " " + x.Category + " (" + x.Subject + "): "
                    + (x.Count > 1 ? x.Count + "x " : "") + x.Message + (x.Fix.Length > 0 ? " Fix: " + x.Fix : "");
                if (x.Severity == Sev.Info) Core.Log?.Msg(line); else Core.Log?.Warning(line);
            }
            if (Total > Shown) Core.Log?.Msg("[Doctor] " + (Total - Shown) + " more not shown (limit " + Shown + ").");
        }

        internal static string Summary()
        {
            return Total + " finding(s), " + Shown + " shown, " + Properties + " propert(ies), " + Employees + " employee(s), "
                + Routes + " route(s), " + ReachTested + " path-tested, " + ElapsedMs + " ms";
        }

        private static string Safe(Prop p) { return p == null ? "?" : p.PropertyName ?? "?"; }
        private static string EmpName(Employee e) { return e == null ? "?" : e.FullName ?? "?"; }
        private static string EntityName(ITransitEntity t) { return t?.Name ?? "?"; }

#if DEBUG
        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("doctor")) return;
            if (!Run(out string refusal)) { Core.Log?.Warning("[Dev] [Doctor] " + refusal); return; }
            Core.Log?.Msg("[Dev] [Doctor] " + Summary());
        }
#endif
    }
}
