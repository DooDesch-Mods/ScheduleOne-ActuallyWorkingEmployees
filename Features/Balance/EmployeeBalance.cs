using System;
using System.Collections.Generic;
using Il2CppScheduleOne.Management;
using ActuallyWorkingEmployees.Config;
using NpcBehaviour = Il2CppScheduleOne.NPCs.Behaviour.Behaviour;

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
            Eff.Add(Options.BotanistDailyWage, () => CommonEmployee.LiveMoney(First(), NetworkSingleton<EmployeeManager>.Instance?.BotanistPrefab, false));
            Eff.Add(Options.BotanistSigningFee, () => CommonEmployee.LiveMoney(First(), NetworkSingleton<EmployeeManager>.Instance?.BotanistPrefab, true));
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
            Botanist prefab = mgr != null ? mgr.BotanistPrefab : null;
            try { CommonEmployee.Prefab(prefab, "botanist", Options.BotanistDailyWage, Options.BotanistSigningFee); }
            catch (Exception ex) { Core.Failed(Name + ": prefab", ex); }
            List<Botanist> list = Roster.Botanists;
            for (int i = 0; i < list.Count; i++)
            {
                Botanist e = list[i];
                try { SyncOne(e, prefab); }
                catch (Exception ex) { Core.Failed(Name + ": employee " + e.name, ex); }
            }
        }

        private static void SyncOne(Botanist e, Employee prefab)
        {
            CommonEmployee.Walk(e, Options.BotanistWalkSpeed);
            CommonEmployee.Money(e, prefab, Options.BotanistDailyWage, Options.BotanistSigningFee);
            if (e.configuration == null || e.configuration.Assigns == null) { Throttle.Warn("bal-botanist: config", "Botanist " + e.name + " has no configuration yet."); return; }
            CommonEmployee.SetMax(e.configuration.Assigns, e, VSlot.Max, Options.BotanistMaxPots);
        }

        protected override void PerFrame()
        {
            DriveAll(Roster.Botanists, Options.BotanistWorkSpeed.Value, Specs);
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
            Eff.Add(Options.ChemistDailyWage, () => CommonEmployee.LiveMoney(First(), NetworkSingleton<EmployeeManager>.Instance?.ChemistPrefab, false));
            Eff.Add(Options.ChemistSigningFee, () => CommonEmployee.LiveMoney(First(), NetworkSingleton<EmployeeManager>.Instance?.ChemistPrefab, true));
            Eff.Add(Options.ChemistMaxStations, () => First() != null && First().configuration != null ? First().configuration.Stations.MaxItems.ToString() : "no chemist");
            Eff.Add(Options.ChemistWorkSpeed, () => First() != null ? Eff.F(WorkSpeed.AppliedFactor(First())) : "no chemist");
            foreach (ActSpec s in Specs.Values) { FloatOption o = s.Option; Eff.Add(o, () => WorkSpeed.LiveSeconds(o.Key)); }
        }

        private static Chemist First() => Roster.Chemists.Count > 0 ? Roster.Chemists[0] : null;

        protected override void Sync()
        {
            EmployeeManager mgr = NetworkSingleton<EmployeeManager>.Instance;
            Chemist prefab = mgr != null ? mgr.ChemistPrefab : null;
            try { CommonEmployee.Prefab(prefab, "chemist", Options.ChemistDailyWage, Options.ChemistSigningFee); }
            catch (Exception ex) { Core.Failed(Name + ": prefab", ex); }
            List<Chemist> list = Roster.Chemists;
            for (int i = 0; i < list.Count; i++)
            {
                Chemist e = list[i];
                try { SyncOne(e, prefab); }
                catch (Exception ex) { Core.Failed(Name + ": employee " + e.name, ex); }
            }
        }

        private static void SyncOne(Chemist e, Employee prefab)
        {
            CommonEmployee.Walk(e, Options.ChemistWalkSpeed);
            CommonEmployee.Money(e, prefab, Options.ChemistDailyWage, Options.ChemistSigningFee);
            if (e.configuration == null || e.configuration.Stations == null) { Throttle.Warn("bal-chemist: config", "Chemist " + e.name + " has no configuration yet."); return; }
            CommonEmployee.SetMax(e.configuration.Stations, e, VSlot.Max, Options.ChemistMaxStations);
        }

        protected override void PerFrame()
        {
            DriveAll(Roster.Chemists, Options.ChemistWorkSpeed.Value, Specs);
        }

        protected override string Details() => "chemists=" + Roster.Chemists.Count;
    }

    /// <summary>
    /// Packager: walk speed, wage, fee, station limit, work speed and action times. The packaging station and the brick
    /// press divide their literal (5 s and 15 s) by Packager.PackagingSpeedMultiplier, not by the work speed, so
    /// PackagingSpeed and the two action times drive that field; WorkSpeed stays on the work-speed controller.
    /// The route limit is fix7-packager-routes.
    /// </summary>
    internal sealed class PackagerBalance : BalanceFeature
    {
        public override string Name => "bal-packager";

        private static readonly Dictionary<Act, ActSpec> Specs = new Dictionary<Act, ActSpec>
        {
            { Act.MoveItem, new ActSpec(Options.PackagerMoveItemTime, 0.5f) },
        };

        private const float PackagingVanillaSeconds = 5f;
        private const float BrickPressVanillaSeconds = 15f;

        protected override IEnumerable<Option> Watched => new Option[]
        {
            Options.PackagerWalkSpeed, Options.PackagerDailyWage, Options.PackagerSigningFee, Options.PackagerMaxStations,
            Options.PackagerWorkSpeed, Options.PackagerPackagingSpeed, Options.PackagerPackagingStationTime,
            Options.PackagerBrickPressTime, Options.PackagerMoveItemTime,
        };

        protected override void OnInit()
        {
            Eff.Add(Options.PackagerWalkSpeed, () => CommonEmployee.LiveWalk(First()));
            Eff.Add(Options.PackagerDailyWage, () => CommonEmployee.LiveMoney(First(), NetworkSingleton<EmployeeManager>.Instance?.PackagerPrefab, false));
            Eff.Add(Options.PackagerSigningFee, () => CommonEmployee.LiveMoney(First(), NetworkSingleton<EmployeeManager>.Instance?.PackagerPrefab, true));
            Eff.Add(Options.PackagerMaxStations, () => First() != null && First().configuration != null ? First().configuration.Stations.MaxItems.ToString() : "no packager");
            Eff.Add(Options.PackagerWorkSpeed, () => First() != null ? Eff.F(WorkSpeed.AppliedFactor(First())) : "no packager");
            Eff.Add(Options.PackagerPackagingSpeed, () => First() != null ? "multiplier " + Eff.F(First().PackagingSpeedMultiplier) : "no packager");
            Eff.Add(Options.PackagerPackagingStationTime, () => WorkSpeed.LiveSeconds(Options.PackagerPackagingStationTime.Key));
            Eff.Add(Options.PackagerBrickPressTime, () => WorkSpeed.LiveSeconds(Options.PackagerBrickPressTime.Key));
            foreach (ActSpec s in Specs.Values) { FloatOption o = s.Option; Eff.Add(o, () => WorkSpeed.LiveSeconds(o.Key)); }
        }

        private static Packager First() => Roster.Packagers.Count > 0 ? Roster.Packagers[0] : null;

        protected override void Sync()
        {
            EmployeeManager mgr = NetworkSingleton<EmployeeManager>.Instance;
            Packager prefab = mgr != null ? mgr.PackagerPrefab : null;
            try { CommonEmployee.Prefab(prefab, "packager", Options.PackagerDailyWage, Options.PackagerSigningFee); }
            catch (Exception ex) { Core.Failed(Name + ": prefab", ex); }
            List<Packager> list = Roster.Packagers;
            for (int i = 0; i < list.Count; i++)
            {
                Packager e = list[i];
                try { SyncOne(e, prefab); }
                catch (Exception ex) { Core.Failed(Name + ": employee " + e.name, ex); }
            }
        }

        private static void SyncOne(Packager e, Employee prefab)
        {
            CommonEmployee.Walk(e, Options.PackagerWalkSpeed);
            CommonEmployee.Money(e, prefab, Options.PackagerDailyWage, Options.PackagerSigningFee);
            if (e.configuration == null || e.configuration.Stations == null) { Throttle.Warn("bal-packager: config", "Packager " + e.name + " has no configuration yet."); return; }
            CommonEmployee.SetMax(e.configuration.Stations, e, VSlot.Max, Options.PackagerMaxStations);
        }

        protected override void PerFrame()
        {
            float workSpeed = Options.PackagerWorkSpeed.Value;
            DriveAll(Roster.Packagers, workSpeed, Specs);
            List<Packager> list = Roster.Packagers;
            for (int i = 0; i < list.Count; i++)
            {
                Packager e = list[i];
                try { DrivePackaging(e, workSpeed); }
                catch (Exception ex) { Core.Failed(Name + ": packaging speed " + e.name, ex); }
            }
        }

        /// <summary>
        /// Writes the multiplier the packaging coroutines read when a wait starts: the game's own value times PackagingSpeed,
        /// and while the packager runs the station or the press, times vanilla seconds over the wanted seconds. Back at
        /// the default the game's value is written back.
        /// </summary>
        private static void DrivePackaging(Packager e, float workSpeed)
        {
            float live = e.PackagingSpeedMultiplier;
            float want = Vanilla.Of(e, VSlot.PackagingSpeed, live) * Options.PackagerPackagingSpeed.Value;
            NpcBehaviour active = e.Behaviour != null ? e.Behaviour.activeBehaviour : null;
            Act act = ActionKinds.Of(active);
            float speed = Options.PackagerPackagingSpeed.Value;
            if (act == Act.Packaging && Vanilla.Active(Options.PackagerPackagingStationTime))
            {
                want *= PackagingVanillaSeconds / Options.PackagerPackagingStationTime.Value;
                WorkSpeed.Note(Options.PackagerPackagingStationTime.Key, Options.PackagerPackagingStationTime.Value, workSpeed * speed);
            }
            else if (act == Act.BrickPress && Vanilla.Active(Options.PackagerBrickPressTime))
            {
                want *= BrickPressVanillaSeconds / Options.PackagerBrickPressTime.Value;
                WorkSpeed.Note(Options.PackagerBrickPressTime.Key, Options.PackagerBrickPressTime.Value, workSpeed * speed);
            }
            if (Math.Abs(live - want) > 1e-4f) e.PackagingSpeedMultiplier = want;
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
            List<Packager> list = Roster.Packagers;
            for (int i = 0; i < list.Count; i++)
            {
                Packager e = list[i];
                try { SyncOne(e); }
                catch (Exception ex) { Core.Failed(Name + ": routes " + e.name, ex); }
            }
        }

        /// <summary>
        /// A limit below the routes a packager already has keeps those routes and only blocks new ones: the game checks
        /// the limit when a route is added and never removes one.
        /// </summary>
        private void SyncOne(Packager e)
        {
            PackagerConfiguration cfg = e.configuration;
            if (cfg == null || cfg.Routes == null) { Throttle.Warn("fix7: config", "Packager " + e.name + " has no route list yet."); return; }
            int live = cfg.Routes.MaxRoutes;
            int want = Vanilla.Want(e, VSlot.Routes, live, Vanilla.Active(Options.PackagerMaxRoutes), Options.PackagerMaxRoutes.Value);
            if (live != want)
            {
                cfg.Routes.MaxRoutes = want;
                _written++;
                Core.Log?.Msg("[fix7-packager-routes] " + e.name + ": route limit " + live + " -> " + want + ".");
            }
            var routes = cfg.Routes.Routes;
            int have = routes != null ? routes.Count : 0;
            if (have > want)
                Throttle.Info("fix7: over limit " + e.name, e.name + " has " + have + " routes and the limit is " + want + ": the routes stay, no new route can be added.", 300f);
        }

        protected override void OnReset() { _written = 0; }

        protected override string Details() => "packagers=" + Roster.Packagers.Count + " writes=" + _written;
    }

    /// <summary>
    /// Cleaner: walk speed, wage, fee, bin limit and the action times. The cleaner behaviours wait with literal
    /// WaitForSeconds and read no work speed, so the times are scaled in <see cref="CleanerWaitPatches"/>.
    /// </summary>
    internal sealed class CleanerBalance : BalanceFeature
    {
        public override string Name => "bal-cleaner";

        protected override IEnumerable<Option> Watched => new Option[]
        {
            Options.CleanerWalkSpeed, Options.CleanerDailyWage, Options.CleanerSigningFee, Options.CleanerMaxBins,
            Options.CleanerWorkSpeed, Options.CleanerTrashPickupTime, Options.CleanerBagTime, Options.CleanerDisposeTime,
            Options.CleanerEmptyGrabberTime,
        };

        protected override void OnInit()
        {
            Eff.Add(Options.CleanerWalkSpeed, () => CommonEmployee.LiveWalk(First()));
            Eff.Add(Options.CleanerDailyWage, () => CommonEmployee.LiveMoney(First(), NetworkSingleton<EmployeeManager>.Instance?.CleanerPrefab, false));
            Eff.Add(Options.CleanerSigningFee, () => CommonEmployee.LiveMoney(First(), NetworkSingleton<EmployeeManager>.Instance?.CleanerPrefab, true));
            Eff.Add(Options.CleanerMaxBins, () => First() != null && First().configuration != null ? First().configuration.Bins.MaxItems.ToString() : "no cleaner");
            Eff.Add(Options.CleanerWorkSpeed, () => CleanerWaits.Seen(CleanerWaits.Kind.Any));
            Eff.Add(Options.CleanerTrashPickupTime, () => CleanerWaits.Seen(CleanerWaits.Kind.Pickup));
            Eff.Add(Options.CleanerBagTime, () => CleanerWaits.Seen(CleanerWaits.Kind.Bag));
            Eff.Add(Options.CleanerDisposeTime, () => CleanerWaits.Seen(CleanerWaits.Kind.Dispose));
            Eff.Add(Options.CleanerEmptyGrabberTime, () => CleanerWaits.Seen(CleanerWaits.Kind.Grabber));
        }

        private static Cleaner First() => Roster.Cleaners.Count > 0 ? Roster.Cleaners[0] : null;

        protected override void Sync()
        {
            EmployeeManager mgr = NetworkSingleton<EmployeeManager>.Instance;
            Cleaner prefab = mgr != null ? mgr.CleanerPrefab : null;
            try { CommonEmployee.Prefab(prefab, "cleaner", Options.CleanerDailyWage, Options.CleanerSigningFee); }
            catch (Exception ex) { Core.Failed(Name + ": prefab", ex); }
            List<Cleaner> list = Roster.Cleaners;
            for (int i = 0; i < list.Count; i++)
            {
                Cleaner e = list[i];
                try { SyncOne(e, prefab); }
                catch (Exception ex) { Core.Failed(Name + ": employee " + e.name, ex); }
            }
        }

        private static void SyncOne(Cleaner e, Employee prefab)
        {
            CommonEmployee.Walk(e, Options.CleanerWalkSpeed);
            CommonEmployee.Money(e, prefab, Options.CleanerDailyWage, Options.CleanerSigningFee);
            if (e.configuration == null || e.configuration.Bins == null) { Throttle.Warn("bal-cleaner: config", "Cleaner " + e.name + " has no configuration yet."); return; }
            CommonEmployee.SetMax(e.configuration.Bins, e, VSlot.Max, Options.CleanerMaxBins);
        }

        protected override string Details() => "cleaners=" + Roster.Cleaners.Count;
    }
}
