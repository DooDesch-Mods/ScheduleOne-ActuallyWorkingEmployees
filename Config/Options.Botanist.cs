namespace ActuallyWorkingEmployees.Config
{
    public static partial class Options
    {
        public static readonly FloatOption BotanistWalkSpeed = new FloatOption("Botanist", "WalkSpeed",
            "Walk speed", "Botanist walking speed. Vanilla: the game's value.",
            -1f, 0.5f, 6f, 0.1f, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption BotanistDailyWage = new IntOption("Botanist", "DailyWage",
            "Daily wage", "Botanist daily wage. Vanilla: the game's value.",
            -1, 0, 10000, 10, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption BotanistSigningFee = new IntOption("Botanist", "SigningFee",
            "Signing fee", "One-time cost to hire a botanist. Vanilla: the game's value.",
            -1, 0, 100000, 50, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption BotanistMaxPots = new IntOption("Botanist", "MaxPots",
            "Max pots", "Pots one botanist can be assigned. Vanilla: 8.",
            8, 1, 32, 1, hostOnly: true, balance: true);

        public static readonly FloatOption BotanistWorkSpeed = new FloatOption("Botanist", "WorkSpeed",
            "Work speed", "Multiplier for the speed of every botanist action. Vanilla: 1.",
            1f, 0.25f, 10f, 0.05f, hostOnly: true, balance: true);

        public static readonly FloatOption BotanistSoilTime = new FloatOption("Botanist", "SoilTime",
            "Add soil time", "Seconds to add soil to a pot. Vanilla: 10.",
            10f, 0.5f, 120f, 0.5f, hostOnly: true, balance: true);

        public static readonly FloatOption BotanistWaterTime = new FloatOption("Botanist", "WaterTime",
            "Water time", "Seconds to water a pot or mushroom bed. Vanilla: 10.",
            10f, 0.5f, 120f, 0.5f, hostOnly: true, balance: true);

        public static readonly FloatOption BotanistAdditiveTime = new FloatOption("Botanist", "AdditiveTime",
            "Additive time", "Seconds to apply an additive to a pot. Vanilla: 10.",
            10f, 0.5f, 120f, 0.5f, hostOnly: true, balance: true);

        public static readonly FloatOption BotanistSowTime = new FloatOption("Botanist", "SowTime",
            "Sow seed time", "Seconds to sow a seed in a pot. Vanilla: 15.",
            15f, 0.5f, 120f, 0.5f, hostOnly: true, balance: true);

        public static readonly FloatOption BotanistHarvestTimePerUnit = new FloatOption("Botanist", "HarvestTimePerUnit",
            "Harvest time per unit", "Seconds to harvest one unit from a pot. Vanilla: 1.",
            1f, 0.1f, 30f, 0.1f, hostOnly: true, balance: true);

        public static readonly FloatOption BotanistDryingRackTime = new FloatOption("Botanist", "DryingRackTime",
            "Drying rack start time", "Seconds to start a drying rack. Vanilla: 1.",
            1f, 0.1f, 30f, 0.1f, hostOnly: true, balance: true);

        public static readonly FloatOption BotanistMushroomBedTime = new FloatOption("Botanist", "MushroomBedTime",
            "Mushroom bed spawn time", "Seconds to apply spawn to a mushroom bed. Vanilla: 15.",
            15f, 0.5f, 120f, 0.5f, hostOnly: true, balance: true);

        public static readonly FloatOption BotanistMushroomHarvestTimePerUnit = new FloatOption("Botanist", "MushroomHarvestTimePerUnit",
            "Mushroom harvest time per unit", "Seconds to harvest one unit from a mushroom bed. Vanilla: 1.",
            1f, 0.1f, 30f, 0.1f, hostOnly: true, balance: true);

        public static readonly FloatOption BotanistSpawnStationTime = new FloatOption("Botanist", "SpawnStationTime",
            "Spawn station time", "Seconds to use a spawn station. Vanilla: 6.",
            6f, 0.5f, 60f, 0.5f, hostOnly: true, balance: true);

        public static readonly FloatOption BotanistCriticalWaterLevel = new FloatOption("Botanist", "CriticalWaterLevel",
            "Critical water level", "Moisture below which a pot counts as urgent and is watered first. Vanilla: 0.2.",
            0.2f, 0.05f, 0.9f, 0.05f, hostOnly: true, balance: true);

        public static readonly FloatOption BotanistWaterLevel = new FloatOption("Botanist", "WaterLevel",
            "Water level", "Moisture below which a pot is watered. Vanilla: 0.3.",
            0.3f, 0.05f, 1f, 0.05f, hostOnly: true, balance: true);

        public static readonly BoolOption BotanistUseSprinklers = new BoolOption("Botanist", "UseSprinklers",
            "Leave sprinkler pots alone", "The botanist does not water pots a placed sprinkler covers. Vanilla: off.",
            false, hostOnly: true, balance: true);
    }
}
