using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Growing;
using Il2CppScheduleOne.ObjectScripts;
using ActuallyWorkingEmployees.Config;
using ItemList = Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.EntityFramework.BuildableItem>;

namespace ActuallyWorkingEmployees.Features.Balance
{
    /// <summary>
    /// Station and growth speeds. Pots, mushroom colonies, cauldrons and mixing stations carry the value in a field, which
    /// is written on the live object (the config constructors are native-called, so a patch on them never binds). Drying
    /// racks and lab ovens read the speed in the DryingRack.GetDryMultiplier and OvenCookOperation.GetCookDuration
    /// postfixes (see <see cref="StationPatches"/>).
    /// </summary>
    internal sealed class StationBalance : BalanceFeature
    {
        public override string Name => "bal-stations";
        private int _pots, _colonies, _cauldrons, _mixers;

        protected override IEnumerable<Option> Watched => new Option[]
        {
            Options.EnableStationSpeed, Options.PlantGrowthSpeed, Options.MushroomGrowthSpeed, Options.DryingRackSpeed,
            Options.CauldronSpeed, Options.LabOvenSpeed, Options.MixingStationSpeed, Options.MixingStationMaxBatch,
        };

        protected override void OnInit()
        {
            Eff.Add(Options.EnableStationSpeed, () => Options.EnableStationSpeed.Value ? "on" : "off");
            Eff.Add(Options.PlantGrowthSpeed, () => First<Pot>() is Pot p ? Eff.F(p.GrowSpeedMultiplier) : "no pot");
            Eff.Add(Options.MushroomGrowthSpeed, () => FirstColony() is ShroomColony c ? c._growTime + " min for full growth" : "no colony");
            Eff.Add(Options.DryingRackSpeed, () => StationPatches.Seen("rack"));
            Eff.Add(Options.CauldronSpeed, () => First<Cauldron>() is Cauldron c ? "cook time " + c.CookTime : "no cauldron");
            Eff.Add(Options.LabOvenSpeed, () => StationPatches.Seen("oven"));
            Eff.Add(Options.MixingStationSpeed, () => First<MixingStation>() is MixingStation m ? "time per item " + m.MixTimePerItem : "no mixing station");
            Eff.Add(Options.MixingStationMaxBatch, () => First<MixingStation>() is MixingStation m ? m.MaxMixQuantity.ToString() : "no mixing station");
        }

        /// <summary>First placed item of type T, for the dev status commands only.</summary>
        private static T First<T>() where T : Il2CppObjectBase
        {
            var lists = new List<ItemList>();
            CollectSources(lists);
            foreach (ItemList list in lists)
                for (int i = 0; i < list.Count; i++)
                {
                    BuildableItem item = list[i];
                    T hit = item?.TryCast<T>();
                    if (hit != null) return hit;
                }
            return null;
        }

        private static ShroomColony FirstColony()
        {
            MushroomBed bed = First<MushroomBed>();
            return bed?.CurrentColony;
        }

        /// <summary>A speed of 1 or a disabled group is vanilla.</summary>
        internal static float Speed(FloatOption o) => Options.EnableStationSpeed.Value ? o.Value : 1f;

        /// <summary>True when any station option differs from the game's value.</summary>
        private static bool AnyActive() =>
            Math.Abs(Speed(Options.PlantGrowthSpeed) - 1f) > 1e-4f
            || Math.Abs(Speed(Options.MushroomGrowthSpeed) - 1f) > 1e-4f
            || Math.Abs(Speed(Options.CauldronSpeed) - 1f) > 1e-4f
            || Math.Abs(Speed(Options.MixingStationSpeed) - 1f) > 1e-4f
            || (Options.EnableStationSpeed.Value && Vanilla.Active(Options.MixingStationMaxBatch));

        /// <summary>Every item list the game keeps: one per property and one per business.</summary>
        private static void CollectSources(List<ItemList> into)
        {
            into.Clear();
            var properties = Il2CppScheduleOne.Property.Property.Properties;
            if (properties == null) Throttle.Warn("bal-stations: properties", "Property.Properties is null; stations are not balanced.");
            else
                for (int i = 0; i < properties.Count; i++)
                {
                    Il2CppScheduleOne.Property.Property p = properties[i];
                    if (p == null) continue;
                    ItemList items = p.BuildableItems;
                    if (items == null) Throttle.Warn("bal-stations: items " + i, "Property " + p.name + " has no BuildableItems list.");
                    else into.Add(items);
                }
            var businesses = Il2CppScheduleOne.Property.Business.Businesses;
            if (businesses == null) Throttle.Warn("bal-stations: businesses", "Business.Businesses is null; business stations are not balanced.");
            else
                for (int i = 0; i < businesses.Count; i++)
                {
                    Il2CppScheduleOne.Property.Business b = businesses[i];
                    if (b == null) continue;
                    ItemList items = b.BuildableItems;
                    if (items == null) Throttle.Warn("bal-stations: business items " + i, "Business " + b.name + " has no BuildableItems list.");
                    else into.Add(items);
                }
        }

        // The sweep over the placed items runs a few dozen items per frame, so a large base never costs one long frame.
        private const int ItemsPerFrame = 48;
        private readonly List<ItemList> _sources = new List<ItemList>();
        private readonly HashSet<IntPtr> _seen = new HashSet<IntPtr>();
        private bool _sweeping, _sweepActive, _touched, _idleLogged;
        private int _srcIdx, _itemIdx;
        private int _runPots, _runColonies, _runCauldrons, _runMixers;

        protected override void Sync()
        {
            if (!_touched && !AnyActive())
            {
                _sweeping = false;
                if (!_idleLogged)
                {
                    _idleLogged = true;
                    Core.Log?.Msg("[bal-stations] every station option is vanilla and nothing was changed; no scan until an option changes.");
                }
                return;
            }
            _idleLogged = false;
            CollectSources(_sources);
            _seen.Clear();
            _srcIdx = 0;
            _itemIdx = 0;
            _runPots = _runColonies = _runCauldrons = _runMixers = 0;
            _sweepActive = false;
            _sweeping = true;
        }

        protected override void PerFrame()
        {
            if (!_sweeping) return;
            if (!_sweepActive && AnyActive()) _sweepActive = true;
            int budget = ItemsPerFrame;
            while (budget > 0 && _srcIdx < _sources.Count)
            {
                ItemList list = _sources[_srcIdx];
                if (_itemIdx >= list.Count) { _srcIdx++; _itemIdx = 0; continue; }
                BuildableItem item = list[_itemIdx++];
                budget--;
                if (item == null || !_seen.Add(item.Pointer)) continue;
                try { ApplyItem(item); }
                catch (Exception e) { Core.Failed(Name + ": item", e); }
            }
            if (_srcIdx < _sources.Count) return;

            _sweeping = false;
            _pots = _runPots; _colonies = _runColonies; _cauldrons = _runCauldrons; _mixers = _runMixers;
            // A pass that ended with every option vanilla and wrote nothing non-vanilla has restored everything.
            _touched = _sweepActive;
        }

        protected override void OnReset()
        {
            _sweeping = false;
            _idleLogged = false;
            _sources.Clear();
            _seen.Clear();
        }

        private void ApplyItem(BuildableItem item)
        {
            Pot pot = item.TryCast<Pot>();
            if (pot != null) { ApplyPot(pot); _runPots++; return; }
            MushroomBed bed = item.TryCast<MushroomBed>();
            if (bed != null)
            {
                ShroomColony colony = bed.CurrentColony;
                if (colony != null) { ApplyColony(colony); _runColonies++; }
                return;
            }
            Cauldron cauldron = item.TryCast<Cauldron>();
            if (cauldron != null) { ApplyCauldron(cauldron); _runCauldrons++; return; }
            MixingStation mixer = item.TryCast<MixingStation>();
            if (mixer != null) { ApplyMixer(mixer); _runMixers++; }
        }

        private static void ApplyPot(Pot p)
        {
            float s = Speed(Options.PlantGrowthSpeed);
            float live = p.GrowSpeedMultiplier;
            float want = Vanilla.Want(p, VSlot.Grow, live, Math.Abs(s - 1f) > 1e-4f, Vanilla.Of(p, VSlot.Grow, live) * s);
            if (Math.Abs(live - want) > 1e-4f) p.GrowSpeedMultiplier = want;
        }

        private static void ApplyColony(ShroomColony c)
        {
            float s = Speed(Options.MushroomGrowthSpeed);
            int live = c._growTime;
            int van = Vanilla.Of(c, VSlot.Grow, live);
            int want = Math.Abs(s - 1f) > 1e-4f ? Math.Max(1, (int)Math.Round(van / s)) : van;
            if (live != want) c._growTime = want;
        }

        private static void ApplyCauldron(Cauldron c)
        {
            float s = Speed(Options.CauldronSpeed);
            int live = c.CookTime;
            int van = Vanilla.Of(c, VSlot.Cook, live);
            int want = Math.Abs(s - 1f) > 1e-4f ? Math.Max(1, (int)Math.Round(van / s)) : van;
            if (live != want) c.CookTime = want;
        }

        private static void ApplyMixer(MixingStation m)
        {
            float s = Speed(Options.MixingStationSpeed);
            int liveTime = m.MixTimePerItem;
            int vanTime = Vanilla.Of(m, VSlot.Time, liveTime);
            int wantTime = Math.Abs(s - 1f) > 1e-4f ? Math.Max(1, (int)Math.Round(vanTime / s)) : vanTime;
            if (liveTime != wantTime) m.MixTimePerItem = wantTime;

            int liveMax = m.MaxMixQuantity;
            int wantMax = Vanilla.Want(m, VSlot.Max, liveMax, Options.EnableStationSpeed.Value && Vanilla.Active(Options.MixingStationMaxBatch), Options.MixingStationMaxBatch.Value);
            if (liveMax != wantMax) m.MaxMixQuantity = wantMax;
        }

        protected override string Details() => "pots=" + _pots + " colonies=" + _colonies + " cauldrons=" + _cauldrons + " mixers=" + _mixers
            + (_touched ? " active" : " idle");
    }

    /// <summary>Employees per property. 0 and a disabled override leave every property at its own value.</summary>
    internal sealed class PropertyBalance : BalanceFeature
    {
        public override string Name => "bal-property";
        private int _properties;

        protected override IEnumerable<Option> Watched => new Option[] { Options.EnableEmployeeCapacityOverride, Options.EmployeeCapacity, Options.EmployeeCapacityLossGuard };

        protected override void OnInit()
        {
            Eff.Add(Options.EmployeeCapacity, () =>
            {
                var list = Il2CppScheduleOne.Property.Property.Properties;
                return list != null && list.Count > 0 ? list[0].EmployeeCapacity.ToString() : "no property";
            });
            Eff.Add(Options.EnableEmployeeCapacityOverride, () => Options.EnableEmployeeCapacityOverride.Value ? "on" : "off");
            Eff.Add(Options.EmployeeCapacityLossGuard, () => Options.EmployeeCapacityLossGuard.Value ? "on" : "off");
        }

        protected override void Sync()
        {
            var list = Il2CppScheduleOne.Property.Property.Properties;
            if (list == null) { Throttle.Warn("bal-property: list", "Property.Properties is null; capacity is not applied."); return; }
            _properties = list.Count;
            bool active = Options.EnableEmployeeCapacityOverride.Value && Options.EmployeeCapacity.Value > 0;
            for (int i = 0; i < list.Count; i++)
            {
                Il2CppScheduleOne.Property.Property prop = list[i];
                if (prop == null) continue;
                try { SyncProperty(prop, active); }
                catch (Exception ex) { Core.Failed(Name + ": property " + prop.name, ex); }
            }
        }

        private static void SyncProperty(Il2CppScheduleOne.Property.Property prop, bool active)
        {
            int live = prop.EmployeeCapacity;
            int van = Vanilla.Of(prop, VSlot.Capacity, live);
            int want = active ? Options.EmployeeCapacity.Value : van;
            if (live == want) return;
            int hired = prop.Employees != null ? prop.Employees.Count : 0;
            if (Options.EmployeeCapacityLossGuard.Value && want < hired)
            {
                Throttle.Warn("bal-property: guard " + prop.name, prop.name + " keeps capacity " + live + ": it has " + hired + " employees and the new limit is " + want + ".");
                return;
            }
            prop.EmployeeCapacity = want;
            Core.Log?.Msg("[bal-property] " + prop.name + ": capacity " + live + " -> " + want + ".");
        }

        protected override string Details() => "properties=" + _properties;
    }

    /// <summary>
    /// Fix 8: harvest time is the per-unit option times the units harvested. The vanilla duration is 1 s times the unit
    /// count, so scaling that result keeps the unit count in it and never turns it into a fixed total.
    /// </summary>
    internal sealed class HarvestTime : BalanceFeature
    {
        public override string Name => "fix8-harvest-time";

        protected override IEnumerable<Option> Watched => new Option[] { Options.BotanistHarvestTimePerUnit, Options.BotanistMushroomHarvestTimePerUnit };

        protected override void OnInit()
        {
            Eff.Add(Options.BotanistHarvestTimePerUnit, () => DurationPatches.Seen(Options.BotanistHarvestTimePerUnit));
            Eff.Add(Options.BotanistMushroomHarvestTimePerUnit, () => DurationPatches.Seen(Options.BotanistMushroomHarvestTimePerUnit));
        }

        protected override void Sync() { }

        protected override string Details() => "pot=" + Eff.F(Options.BotanistHarvestTimePerUnit.Value) + "s/unit mushroom=" + Eff.F(Options.BotanistMushroomHarvestTimePerUnit.Value) + "s/unit";
    }
}
