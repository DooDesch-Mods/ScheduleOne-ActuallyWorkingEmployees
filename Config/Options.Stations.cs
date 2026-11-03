namespace ActuallyWorkingEmployees.Config
{
    public static partial class Options
    {
        public static readonly BoolOption EnableStationSpeed = new BoolOption("Stations", "EnableStationSpeed",
            "Enable station and growth speed", "Turns on the station and growth speed settings below. Vanilla: off.",
            false, hostOnly: true, balance: true);

        public static readonly FloatOption PlantGrowthSpeed = new FloatOption("Stations", "PlantGrowthSpeed",
            "Plant growth speed", "Speed multiplier for plant growth. Vanilla: 1.",
            1f, 0.25f, 10f, 0.05f, hostOnly: true, balance: true);

        public static readonly FloatOption MushroomGrowthSpeed = new FloatOption("Stations", "MushroomGrowthSpeed",
            "Mushroom growth speed", "Speed multiplier for mushroom growth. Vanilla: 1.",
            1f, 0.25f, 10f, 0.05f, hostOnly: true, balance: true);

        public static readonly FloatOption DryingRackSpeed = new FloatOption("Stations", "DryingRackSpeed",
            "Drying rack speed", "Speed multiplier for drying racks. Vanilla: 1.",
            1f, 0.25f, 10f, 0.05f, hostOnly: true, balance: true);

        public static readonly FloatOption CauldronSpeed = new FloatOption("Stations", "CauldronSpeed",
            "Cauldron cook speed", "Speed multiplier for cauldron cooking. Vanilla: 1.",
            1f, 0.25f, 10f, 0.05f, hostOnly: true, balance: true);

        public static readonly FloatOption LabOvenSpeed = new FloatOption("Stations", "LabOvenSpeed",
            "Lab oven speed", "Speed multiplier for lab ovens. Vanilla: 1.",
            1f, 0.25f, 10f, 0.05f, hostOnly: true, balance: true);

        public static readonly FloatOption MixingStationSpeed = new FloatOption("Stations", "MixingStationSpeed",
            "Mixing station speed", "Speed multiplier for mixing stations. Vanilla: 1.",
            1f, 0.25f, 10f, 0.05f, hostOnly: true, balance: true);

        public static readonly IntOption MixingStationMaxBatch = new IntOption("Stations", "MixingStationMaxBatch",
            "Max mix batch", "Largest batch a mixing station takes. Vanilla: 10.",
            10, 1, 20, 1, hostOnly: true, balance: true);

        public static readonly BoolOption ShowStationSpeedWarning = new BoolOption("Stations", "ShowStationSpeedWarning",
            "Warn about saved mixes", "Shows a hint that saved mixes keep their old time after a speed change.",
            true, hostOnly: false, balance: false);
    }
}
