using System;
using System.Collections.Generic;
using Il2CppScheduleOne.Growing;
using Il2CppScheduleOne.ObjectScripts;
using ActuallyWorkingEmployees.Config;

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
            Eff.Add(Options.MushroomGrowthSpeed, () => First<ShroomColony>() is ShroomColony c ? c._growTime + " min for full growth" : "no colony");
            Eff.Add(Options.DryingRackSpeed, () => StationPatches.Seen("rack"));
            Eff.Add(Options.CauldronSpeed, () => First<Cauldron>() is Cauldron c ? "cook time " + c.CookTime : "no cauldron");
            Eff.Add(Options.LabOvenSpeed, () => StationPatches.Seen("oven"));
            Eff.Add(Options.MixingStationSpeed, () => First<MixingStation>() is MixingStation m ? "time per item " + m.MixTimePerItem : "no mixing station");
            Eff.Add(Options.MixingStationMaxBatch, () => First<MixingStation>() is MixingStation m ? m.MaxMixQuantity.ToString() : "no mixing station");
        }

        private static T First<T>() where T : UnityEngine.Object
        {
            var all = UnityEngine.Object.FindObjectsOfType<T>();
            return all != null && all.Length > 0 ? all[0] : null;
        }

        /// <summary>A speed of 1 or a disabled group is vanilla.</summary>
        internal static float Speed(FloatOption o) => Options.EnableStationSpeed.Value ? o.Value : 1f;

        protected override void Sync()
        {
            Guarded("pots", () =>
            {
                float s = Speed(Options.PlantGrowthSpeed);
                var all = UnityEngine.Object.FindObjectsOfType<Pot>();
                _pots = all.Length;
                foreach (Pot p in all)
                {
                    float live = p.GrowSpeedMultiplier;
                    float want = Vanilla.Want(p, "grow", live, Math.Abs(s - 1f) > 1e-4f, Vanilla.Of(p, "grow", live) * s);
                    if (Math.Abs(live - want) > 1e-4f) p.GrowSpeedMultiplier = want;
                }
            });
            Guarded("colonies", () =>
            {
                float s = Speed(Options.MushroomGrowthSpeed);
                var all = UnityEngine.Object.FindObjectsOfType<ShroomColony>();
                _colonies = all.Length;
                foreach (ShroomColony c in all)
                {
                    int live = c._growTime;
                    int van = Vanilla.Of(c, "grow", live);
                    int want = Math.Abs(s - 1f) > 1e-4f ? Math.Max(1, (int)Math.Round(van / s)) : van;
                    if (live != want) c._growTime = want;
                }
            });
            Guarded("cauldrons", () =>
            {
                float s = Speed(Options.CauldronSpeed);
                var all = UnityEngine.Object.FindObjectsOfType<Cauldron>();
                _cauldrons = all.Length;
                foreach (Cauldron c in all)
                {
                    int live = c.CookTime;
                    int van = Vanilla.Of(c, "cook", live);
                    int want = Math.Abs(s - 1f) > 1e-4f ? Math.Max(1, (int)Math.Round(van / s)) : van;
                    if (live != want) c.CookTime = want;
                }
            });
            Guarded("mixing stations", () =>
            {
                float s = Speed(Options.MixingStationSpeed);
                var all = UnityEngine.Object.FindObjectsOfType<MixingStation>();
                _mixers = all.Length;
                foreach (MixingStation m in all)
                {
                    int liveTime = m.MixTimePerItem;
                    int vanTime = Vanilla.Of(m, "time", liveTime);
                    int wantTime = Math.Abs(s - 1f) > 1e-4f ? Math.Max(1, (int)Math.Round(vanTime / s)) : vanTime;
                    if (liveTime != wantTime) m.MixTimePerItem = wantTime;

                    int liveMax = m.MaxMixQuantity;
                    int wantMax = Vanilla.Want(m, "max", liveMax, Options.EnableStationSpeed.Value && Vanilla.Active(Options.MixingStationMaxBatch), Options.MixingStationMaxBatch.Value);
                    if (liveMax != wantMax) m.MaxMixQuantity = wantMax;
                }
            });
        }

        protected override string Details() => "pots=" + _pots + " colonies=" + _colonies + " cauldrons=" + _cauldrons + " mixers=" + _mixers;
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
                Il2CppScheduleOne.Property.Property p = list[i];
                if (p == null) continue;
                Il2CppScheduleOne.Property.Property prop = p;
                Guarded("property " + prop.name, () =>
                {
                    int live = prop.EmployeeCapacity;
                    int van = Vanilla.Of(prop, "capacity", live);
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
                });
            }
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
