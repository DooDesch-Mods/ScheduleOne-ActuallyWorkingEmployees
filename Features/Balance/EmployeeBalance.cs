using System;
using System.Collections.Generic;
using Il2CppScheduleOne.Management;
using ActuallyWorkingEmployees.Config;

namespace ActuallyWorkingEmployees.Features.Balance
{
    /// <summary>Botanist: walk speed, wage, fee, pot limit, work speed and the action times that are not harvests.</summary>
    internal sealed class BotanistBalance : BalanceFeature
    {
        public override string Name => "bal-botanist";

        private static readonly Dictionary<Act, ActSpec> Specs = new Dictionary<Act, ActSpec>
        {
            { Act.DryingStart, new ActSpec(Options.BotanistDryingRackTime, 1f) },
            { Act.SpawnStation, new ActSpec(Options.BotanistSpawnStationTime, 6f) },
        };

        protected override IEnumerable<Option> Watched => new Option[]
        {
            Options.BotanistWalkSpeed, Options.BotanistDailyWage, Options.BotanistSigningFee, Options.BotanistMaxPots,
            Options.BotanistWorkSpeed, Options.BotanistDryingRackTime, Options.BotanistSpawnStationTime,
        };

        protected override void OnInit()
        {
            Eff.Add(Options.BotanistWalkSpeed, () => CommonEmployee.LiveWalk(First()));
            Eff.Add(Options.BotanistDailyWage, () => First() != null ? Eff.F(First().DailyWage) : "no botanist");
            Eff.Add(Options.BotanistSigningFee, () => First() != null ? Eff.F(First().SigningFee) : "no botanist");
            Eff.Add(Options.BotanistMaxPots, () => First() != null && First().configuration != null ? First().configuration.Assigns.MaxItems.ToString() : "no botanist");
            Eff.Add(Options.BotanistWorkSpeed, () => First() != null ? Eff.F(WorkSpeed.AppliedFactor(First())) : "no botanist");
            Eff.Add(Options.BotanistDryingRackTime, () => WorkSpeed.LiveSeconds(Options.BotanistDryingRackTime.Key));
            Eff.Add(Options.BotanistSpawnStationTime, () => WorkSpeed.LiveSeconds(Options.BotanistSpawnStationTime.Key));
            Eff.Add(Options.BotanistSoilTime, () => DurationPatches.Seen(Options.BotanistSoilTime));
            Eff.Add(Options.BotanistWaterTime, () => DurationPatches.Seen(Options.BotanistWaterTime));
            Eff.Add(Options.BotanistAdditiveTime, () => DurationPatches.Seen(Options.BotanistAdditiveTime));
            Eff.Add(Options.BotanistSowTime, () => DurationPatches.Seen(Options.BotanistSowTime));
            Eff.Add(Options.BotanistMushroomBedTime, () => DurationPatches.Seen(Options.BotanistMushroomBedTime));
            Eff.Add(Options.BotanistCriticalWaterLevel, () => DurationPatches.SeenThreshold("critical"));
            Eff.Add(Options.BotanistWaterLevel, () => DurationPatches.SeenThreshold("water"));
        }

        private static Botanist First() => Roster.Botanists.Count > 0 ? Roster.Botanists[0] : null;

        protected override void Sync()
        {
            EmployeeManager mgr = NetworkSingleton<EmployeeManager>.Instance;
            if (mgr != null) Guarded("prefab", () => CommonEmployee.Prefab(mgr.BotanistPrefab, "botanist", Options.BotanistDailyWage, Options.BotanistSigningFee));
            foreach (Botanist b in Roster.Botanists)
            {
                Botanist e = b;
                Guarded("employee " + e.name, () =>
                {
                    CommonEmployee.Walk(e, Options.BotanistWalkSpeed);
                    CommonEmployee.Money(e, Options.BotanistDailyWage, Options.BotanistSigningFee);
                    if (e.configuration == null || e.configuration.Assigns == null) { Throttle.Warn("bal-botanist: config", "Botanist " + e.name + " has no configuration yet."); return; }
                    CommonEmployee.SetMax(e.configuration.Assigns, e, "max", Options.BotanistMaxPots);
                });
            }
        }

        protected override void PerFrame()
        {
            foreach (Botanist b in Roster.Botanists)
            {
                Botanist e = b;
                Guarded("work speed " + e.name, () => WorkSpeed.Drive(e, Options.BotanistWorkSpeed.Value, Specs));
            }
        }

        protected override string Details() => "botanists=" + Roster.Botanists.Count;
    }

    /// <summary>Chemist: walk speed, wage, fee, station limit, work speed and the station action times.</summary>
    internal sealed class ChemistBalance : BalanceFeature
    {
        public override string Name => "bal-chemist";

        private static readonly Dictionary<Act, ActSpec> Specs = new Dictionary<Act, ActSpec>
        {
            { Act.LabOvenStart, new ActSpec(Options.ChemistLabOvenStartTime, 5f) },
            { Act.LabOvenFinish, new ActSpec(Options.ChemistLabOvenFinishTime, 10f) },
            { Act.Chemistry, new ActSpec(Options.ChemistChemistryStationTime, 20f) },
            { Act.Mixing, new ActSpec(Options.ChemistMixingStationTime, 1f) },
            { Act.Cauldron, new ActSpec(Options.ChemistCauldronTime, 15f) },
        };

        protected override IEnumerable<Option> Watched => new Option[]
        {
            Options.ChemistWalkSpeed, Options.ChemistDailyWage, Options.ChemistSigningFee, Options.ChemistMaxStations,
            Options.ChemistWorkSpeed, Options.ChemistLabOvenStartTime, Options.ChemistLabOvenFinishTime,
            Options.ChemistChemistryStationTime, Options.ChemistMixingStationTime, Options.ChemistCauldronTime,
        };

        protected override void OnInit()
        {
            Eff.Add(Options.ChemistWalkSpeed, () => CommonEmployee.LiveWalk(First()));
            Eff.Add(Options.ChemistDailyWage, () => First() != null ? Eff.F(First().DailyWage) : "no chemist");
            Eff.Add(Options.ChemistSigningFee, () => First() != null ? Eff.F(First().SigningFee) : "no chemist");
            Eff.Add(Options.ChemistMaxStations, () => First() != null && First().configuration != null ? First().configuration.Stations.MaxItems.ToString() : "no chemist");
            Eff.Add(Options.ChemistWorkSpeed, () => First() != null ? Eff.F(WorkSpeed.AppliedFactor(First())) : "no chemist");
            foreach (ActSpec s in Specs.Values) { FloatOption o = s.Option; Eff.Add(o, () => WorkSpeed.LiveSeconds(o.Key)); }
        }

        private static Chemist First() => Roster.Chemists.Count > 0 ? Roster.Chemists[0] : null;

        protected override void Sync()
        {
            EmployeeManager mgr = NetworkSingleton<EmployeeManager>.Instance;
            if (mgr != null) Guarded("prefab", () => CommonEmployee.Prefab(mgr.ChemistPrefab, "chemist", Options.ChemistDailyWage, Options.ChemistSigningFee));
            foreach (Chemist c in Roster.Chemists)
            {
                Chemist e = c;
                Guarded("employee " + e.name, () =>
                {
                    CommonEmployee.Walk(e, Options.ChemistWalkSpeed);
                    CommonEmployee.Money(e, Options.ChemistDailyWage, Options.ChemistSigningFee);
                    if (e.configuration == null || e.configuration.Stations == null) { Throttle.Warn("bal-chemist: config", "Chemist " + e.name + " has no configuration yet."); return; }
                    CommonEmployee.SetMax(e.configuration.Stations, e, "max", Options.ChemistMaxStations);
                });
            }
        }

        protected override void PerFrame()
        {
            foreach (Chemist c in Roster.Chemists)
            {
                Chemist e = c;
                Guarded("work speed " + e.name, () => WorkSpeed.Drive(e, Options.ChemistWorkSpeed.Value, Specs));
            }
        }

        protected override string Details() => "chemists=" + Roster.Chemists.Count;
    }

    /// <summary>
    /// Packager: walk speed, wage, fee, station limit, work speed and action times. PackagingSpeed multiplies the
    /// same work-speed entry as WorkSpeed, so the two options are one factor and never a second one.
    /// The route limit is fix7-packager-routes.
    /// </summary>
    internal sealed class PackagerBalance : BalanceFeature
    {
        public override string Name => "bal-packager";

        private static readonly Dictionary<Act, ActSpec> Specs = new Dictionary<Act, ActSpec>
        {
            { Act.Packaging, new ActSpec(Options.PackagerPackagingStationTime, 5f) },
            { Act.BrickPress, new ActSpec(Options.PackagerBrickPressTime, 15f) },
            { Act.MoveItem, new ActSpec(Options.PackagerMoveItemTime, 0.5f) },
        };

        protected override IEnumerable<Option> Watched => new Option[]
        {
            Options.PackagerWalkSpeed, Options.PackagerDailyWage, Options.PackagerSigningFee, Options.PackagerMaxStations,
            Options.PackagerWorkSpeed, Options.PackagerPackagingSpeed, Options.PackagerPackagingStationTime,
            Options.PackagerBrickPressTime, Options.PackagerMoveItemTime,
        };

        protected override void OnInit()
        {
            Eff.Add(Options.PackagerWalkSpeed, () => CommonEmployee.LiveWalk(First()));
            Eff.Add(Options.PackagerDailyWage, () => First() != null ? Eff.F(First().DailyWage) : "no packager");
            Eff.Add(Options.PackagerSigningFee, () => First() != null ? Eff.F(First().SigningFee) : "no packager");
            Eff.Add(Options.PackagerMaxStations, () => First() != null && First().configuration != null ? First().configuration.Stations.MaxItems.ToString() : "no packager");
            Eff.Add(Options.PackagerWorkSpeed, () => First() != null ? Eff.F(WorkSpeed.AppliedFactor(First())) : "no packager");
            Eff.Add(Options.PackagerPackagingSpeed, () => First() != null ? Eff.F(WorkSpeed.AppliedFactor(First())) : "no packager");
            foreach (ActSpec s in Specs.Values) { FloatOption o = s.Option; Eff.Add(o, () => WorkSpeed.LiveSeconds(o.Key)); }
        }

        private static Packager First() => Roster.Packagers.Count > 0 ? Roster.Packagers[0] : null;

        protected override void Sync()
        {
            EmployeeManager mgr = NetworkSingleton<EmployeeManager>.Instance;
            if (mgr != null) Guarded("prefab", () => CommonEmployee.Prefab(mgr.PackagerPrefab, "packager", Options.PackagerDailyWage, Options.PackagerSigningFee));
            foreach (Packager p in Roster.Packagers)
            {
                Packager e = p;
                Guarded("employee " + e.name, () =>
                {
                    CommonEmployee.Walk(e, Options.PackagerWalkSpeed);
                    CommonEmployee.Money(e, Options.PackagerDailyWage, Options.PackagerSigningFee);
                    if (e.configuration == null || e.configuration.Stations == null) { Throttle.Warn("bal-packager: config", "Packager " + e.name + " has no configuration yet."); return; }
                    CommonEmployee.SetMax(e.configuration.Stations, e, "max", Options.PackagerMaxStations);
                });
            }
        }

        protected override void PerFrame()
        {
            float baseSpeed = Options.PackagerWorkSpeed.Value * Options.PackagerPackagingSpeed.Value;
            foreach (Packager p in Roster.Packagers)
            {
                Packager e = p;
                Guarded("work speed " + e.name, () => WorkSpeed.Drive(e, baseSpeed, Specs));
            }
        }

        protected override string Details() => "packagers=" + Roster.Packagers.Count;
    }

    /// <summary>
    /// Fix 7: the packager route limit. The game sets RouteListField.MaxRoutes to 5 in the PackagerConfiguration
    /// constructor, which native code calls, so a patch on it never binds. The limit is written on the live field instead.
    /// </summary>
    internal sealed class PackagerRoutes : BalanceFeature
    {
        public override string Name => "fix7-packager-routes";
        private int _written;

        protected override IEnumerable<Option> Watched => new Option[] { Options.PackagerMaxRoutes };

        protected override void OnInit()
        {
            Eff.Add(Options.PackagerMaxRoutes, () => Roster.Packagers.Count > 0 && Roster.Packagers[0].configuration != null
                ? Roster.Packagers[0].configuration.Routes.MaxRoutes.ToString() : "no packager");
        }

        protected override void Sync()
        {
            foreach (Packager p in Roster.Packagers)
            {
                Packager e = p;
                Guarded("routes " + e.name, () =>
                {
                    PackagerConfiguration cfg = e.configuration;
                    if (cfg == null || cfg.Routes == null) { Throttle.Warn("fix7: config", "Packager " + e.name + " has no route list yet."); return; }
                    int live = cfg.Routes.MaxRoutes;
                    int want = Vanilla.Want(e, "routes", live, Vanilla.Active(Options.PackagerMaxRoutes), Options.PackagerMaxRoutes.Value);
                    if (live == want) return;
                    cfg.Routes.MaxRoutes = want;
                    _written++;
                    Core.Log?.Msg("[fix7-packager-routes] " + e.name + ": route limit " + live + " -> " + want + ".");
                });
            }
        }

        protected override void OnReset() { _written = 0; }

        protected override string Details() => "packagers=" + Roster.Packagers.Count + " writes=" + _written;
    }

    /// <summary>Cleaner: walk speed, wage, fee and bin limit. Its action times are not scalable (see Options.Cleaner).</summary>
    internal sealed class CleanerBalance : BalanceFeature
    {
        public override string Name => "bal-cleaner";

        protected override IEnumerable<Option> Watched => new Option[]
        {
            Options.CleanerWalkSpeed, Options.CleanerDailyWage, Options.CleanerSigningFee, Options.CleanerMaxBins,
        };

        protected override void OnInit()
        {
            Eff.Add(Options.CleanerWalkSpeed, () => CommonEmployee.LiveWalk(First()));
            Eff.Add(Options.CleanerDailyWage, () => First() != null ? Eff.F(First().DailyWage) : "no cleaner");
            Eff.Add(Options.CleanerSigningFee, () => First() != null ? Eff.F(First().SigningFee) : "no cleaner");
            Eff.Add(Options.CleanerMaxBins, () => First() != null && First().configuration != null ? First().configuration.Bins.MaxItems.ToString() : "no cleaner");
        }

        private static Cleaner First() => Roster.Cleaners.Count > 0 ? Roster.Cleaners[0] : null;

        protected override void Sync()
        {
            EmployeeManager mgr = NetworkSingleton<EmployeeManager>.Instance;
            if (mgr != null) Guarded("prefab", () => CommonEmployee.Prefab(mgr.CleanerPrefab, "cleaner", Options.CleanerDailyWage, Options.CleanerSigningFee));
            foreach (Cleaner c in Roster.Cleaners)
            {
                Cleaner e = c;
                Guarded("employee " + e.name, () =>
                {
                    CommonEmployee.Walk(e, Options.CleanerWalkSpeed);
                    CommonEmployee.Money(e, Options.CleanerDailyWage, Options.CleanerSigningFee);
                    if (e.configuration == null || e.configuration.Bins == null) { Throttle.Warn("bal-cleaner: config", "Cleaner " + e.name + " has no configuration yet."); return; }
                    CommonEmployee.SetMax(e.configuration.Bins, e, "max", Options.CleanerMaxBins);
                });
            }
        }

        protected override string Details() => "cleaners=" + Roster.Cleaners.Count;
    }
}
