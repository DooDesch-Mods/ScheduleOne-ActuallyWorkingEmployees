namespace ActuallyWorkingEmployees.Config
{
    public static partial class Options
    {
        public static readonly FloatOption ChemistWalkSpeed = new FloatOption("Chemist", "WalkSpeed",
            "Walk speed", "Chemist walking speed. Vanilla: the game's value.",
            -1f, 0.5f, 6f, 0.1f, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption ChemistDailyWage = new IntOption("Chemist", "DailyWage",
            "Daily wage", "Chemist daily wage. Vanilla: the game's value.",
            -1, 0, 10000, 10, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption ChemistSigningFee = new IntOption("Chemist", "SigningFee",
            "Signing fee", "One-time cost to hire a chemist. Vanilla: the game's value.",
            -1, 0, 100000, 50, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption ChemistMaxStations = new IntOption("Chemist", "MaxStations",
            "Max stations", "Stations one chemist can be assigned. Vanilla: 4.",
            4, 1, 32, 1, hostOnly: true, balance: true);

        public static readonly FloatOption ChemistWorkSpeed = new FloatOption("Chemist", "WorkSpeed",
            "Work speed", "Multiplier for the speed of every chemist action. Vanilla: 1.",
            1f, 0.25f, 10f, 0.05f, hostOnly: true, balance: true);

        public static readonly FloatOption ChemistLabOvenStartTime = new FloatOption("Chemist", "LabOvenStartTime",
            "Lab oven load time", "Seconds to load a lab oven. Vanilla: 5.",
            5f, 0.5f, 60f, 0.5f, hostOnly: true, balance: true);

        public static readonly FloatOption ChemistLabOvenFinishTime = new FloatOption("Chemist", "LabOvenFinishTime",
            "Lab oven empty time", "Seconds to empty a lab oven. Vanilla: 10.",
            10f, 0.5f, 60f, 0.5f, hostOnly: true, balance: true);

        public static readonly FloatOption ChemistChemistryStationTime = new FloatOption("Chemist", "ChemistryStationTime",
            "Chemistry station time", "Total seconds to set up a chemistry station. Vanilla: 20.",
            20f, 2f, 120f, 1f, hostOnly: true, balance: true);

        public static readonly FloatOption ChemistMixingStationTime = new FloatOption("Chemist", "MixingStationTime",
            "Mixing station time", "Seconds per ingredient at a mixing station. Vanilla: 1.",
            1f, 0.1f, 30f, 0.1f, hostOnly: true, balance: true);

        public static readonly FloatOption ChemistCauldronTime = new FloatOption("Chemist", "CauldronTime",
            "Cauldron start time", "Seconds to start a cauldron. Vanilla: 15.",
            15f, 0.5f, 120f, 0.5f, hostOnly: true, balance: true);
    }
}
