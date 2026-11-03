namespace ActuallyWorkingEmployees.Config
{
    public static partial class Options
    {
        public static readonly FloatOption CleanerWalkSpeed = new FloatOption("Cleaner", "WalkSpeed",
            "Walk speed", "Cleaner walking speed. Vanilla: the game's value.",
            -1f, 0.5f, 6f, 0.1f, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption CleanerDailyWage = new IntOption("Cleaner", "DailyWage",
            "Daily wage", "Cleaner daily wage. Vanilla: the game's value.",
            -1, 0, 10000, 10, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption CleanerSigningFee = new IntOption("Cleaner", "SigningFee",
            "Signing fee", "One-time cost to hire a cleaner. Vanilla: the game's value.",
            -1, 0, 100000, 50, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption CleanerMaxBins = new IntOption("Cleaner", "MaxBins",
            "Max bins", "Bins one cleaner can be assigned. Vanilla: 6.",
            6, 1, 32, 1, hostOnly: true, balance: true);
    }
}
