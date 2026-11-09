using System;
using System.Globalization;
using MelonLoader;
using ActuallyWorkingEmployees.Networking;

namespace ActuallyWorkingEmployees.Config
{
    public enum OptionKind { Bool, Int, Float, Choice }

    /// <summary>
    /// One player setting. The value is stored in a MelonPreferences entry in category <c>AWE_&lt;Group&gt;</c>, in the
    /// mod's own file (<see cref="OptionsFile"/>); until
    /// <see cref="Options.Initialize"/> has bound the entry, the option reports its default. Text values use the
    /// invariant culture both ways, so the app, the dev console and the co-op sync exchange the same strings.
    ///
    /// On a co-op client a host-only option is read-only: <see cref="TrySet"/> refuses, and the app shows
    /// <see cref="HostValueText"/>, which the sync fills through <see cref="SetHostValue"/>.
    /// </summary>
    public abstract class Option
    {
        /// <summary><c>group.lower + "." + Id</c>, e.g. <c>botanist.walkspeed</c>. Lookups ignore case.</summary>
        public string Key { get; }
        public string Group { get; }
        public string Id { get; }
        public string Label { get; }
        public string Help { get; }
        public abstract OptionKind Kind { get; }

        /// <summary>Applied only by the host; a co-op client reads the host's value and cannot change it.</summary>
        public bool HostOnly { get; }

        /// <summary>A balance option. While <c>general.usevanillabalance</c> is on, its typed value is the default.</summary>
        public bool Balance { get; }

        /// <summary>Raised after the local value or the host value changed.</summary>
        public event Action<Option> Changed;

        private string _hostRaw;

        protected Option(string group, string id, string label, string help, bool hostOnly, bool balance)
        {
            Group = group;
            Id = id;
            Key = group.ToLowerInvariant() + "." + id.ToLowerInvariant();
            Label = label;
            Help = help ?? "";
            HostOnly = hostOnly;
            Balance = balance;
            Options.Pending.Add(this);
        }

        public abstract string DefaultText { get; }
        public abstract string ValueText { get; }

        /// <summary>The stored value as text before any clamp; equal to <see cref="ValueText"/> except for number options.</summary>
        public virtual string RawText => ValueText;

        /// <summary>True when <paramref name="raw"/> is a number outside this option's min and max (the vanilla -1 excluded).</summary>
        public virtual bool IsOutside(string raw) => false;

        /// <summary>Raises <see cref="Changed"/> for a value that changed without a set, e.g. a limit that was lifted.</summary>
        internal void NotifyChanged() => RaiseChanged();

        /// <summary>The host's value as text on a co-op client, otherwise null.</summary>
        public string HostValueText => Net.IsClientOnly ? _hostRaw : null;

        /// <summary>The value a player should see: the host's value on a co-op client, otherwise the local one.</summary>
        public string DisplayText => Describe(HostValueText ?? ValueText);

        public bool IsDefault => ValueText == DefaultText;

        /// <summary>True when this machine may change the value now.</summary>
        public bool Editable => !(HostOnly && Net.IsClientOnly);

        /// <summary>Parses and stores <paramref name="raw"/>. "default" and "vanilla" restore the default.</summary>
        public bool TrySet(string raw, out string error)
        {
            if (!Editable) { error = "The host controls this setting."; return false; }
            string s = (raw ?? "").Trim();
            if (s.Equals("default", StringComparison.OrdinalIgnoreCase) || s.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
                s = DefaultText;
            if (!TryStore(s, out error)) return false;
            if (Core.Verbose) Core.Log?.Msg("[Options] " + Key + " = " + ValueText);
            OptionsFile.MarkDirty();
            RaiseChanged();
            return true;
        }

        public void ResetToDefault()
        {
            if (!TryStore(DefaultText, out string error))
            {
                Core.Log?.Error("[Options] reset " + Key + " failed: " + error);
                return;
            }
            OptionsFile.MarkDirty();
            RaiseChanged();
        }

        /// <summary>Stores the host's value for display on a co-op client. Does not touch the local preference.</summary>
        public void SetHostValue(string raw)
        {
            if (_hostRaw == raw) return;
            _hostRaw = raw;
            RaiseChanged();
        }

        public void ClearHostValue()
        {
            if (_hostRaw == null) return;
            _hostRaw = null;
            RaiseChanged();
        }

        /// <summary>Human text for a stored value, e.g. "On", "Vanilla", "1.5".</summary>
        public abstract string Describe(string raw);

        internal abstract void Bind(MelonPreferences_Category category);

        protected abstract bool TryStore(string raw, out string error);

        private void RaiseChanged()
        {
            Action<Option> h = Changed;
            if (h == null) return;
            try { h(this); }
            catch (Exception e) { Core.Failed("options: Changed handler for " + Key, e); }
        }

        protected static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        protected static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class BoolOption : Option
    {
        public bool Default { get; }
        private MelonPreferences_Entry<bool> _entry;

        public BoolOption(string group, string id, string label, string help, bool def, bool hostOnly = true, bool balance = false)
            : base(group, id, label, help, hostOnly, balance) { Default = def; }

        public override OptionKind Kind => OptionKind.Bool;

        /// <summary>The stored value, or the default for a balance option while vanilla balance is forced.</summary>
        public bool Value => Balance && Options.VanillaForced ? Default : Stored;

        internal bool Stored => _entry != null ? _entry.Value : Default;

        public override string DefaultText => Default ? "true" : "false";
        public override string ValueText => Stored ? "true" : "false";

        public override string Describe(string raw) => TryParse(raw, out bool b) ? (b ? "On" : "Off") : raw;

        internal override void Bind(MelonPreferences_Category category)
            => _entry = category.CreateEntry(Id, Default, Label, Help);

        protected override bool TryStore(string raw, out string error)
        {
            if (!TryParse(raw, out bool b)) { error = Key + ": expected on or off, got '" + raw + "'."; return false; }
            if (_entry != null) _entry.Value = b;
            error = null;
            return true;
        }

        private static bool TryParse(string raw, out bool b)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "true": case "on": case "1": case "yes": b = true; return true;
                case "false": case "off": case "0": case "no": b = false; return true;
                default: b = false; return false;
            }
        }
    }

    public sealed class IntOption : Option
    {
        public int Default { get; }
        public int Min { get; }
        public int Max { get; }
        public int Step { get; }

        /// <summary>-1 is accepted besides the range and means "the game's own value, read at runtime".</summary>
        public bool VanillaSentinel { get; }

        private MelonPreferences_Entry<int> _entry;

        public IntOption(string group, string id, string label, string help, int def, int min, int max, int step = 1,
            bool hostOnly = true, bool balance = true, bool vanillaSentinel = false)
            : base(group, id, label, help, hostOnly, balance)
        {
            Default = def; Min = min; Max = max; Step = step; VanillaSentinel = vanillaSentinel;
        }

        public override OptionKind Kind => OptionKind.Int;

        public int Value => Balance && Options.VanillaForced ? Default : Stored;

        internal int Stored
        {
            get
            {
                if (_entry == null) return Default;
                int v = _entry.Value;
                if (InRange(v)) return v;
                int c = Math.Max(Min, Math.Min(Max, v));
                Core.WarnOnce("options: clamp " + Key, Key + " = " + I(v) + " is outside " + I(Min) + "-" + I(Max) + ", using " + I(c) + ".");
                return c;
            }
        }

        public override string DefaultText => I(Default);
        public override string ValueText => I(Stored);
        public override string RawText => _entry != null ? I(_entry.Value) : DefaultText;

        public override bool IsOutside(string raw)
            => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && !Within(v);

        public override string Describe(string raw)
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return raw;
            return VanillaSentinel && v == -1 ? "Vanilla" : I(v);
        }

        internal override void Bind(MelonPreferences_Category category)
            => _entry = category.CreateEntry(Id, Default, Label, Help);

        protected override bool TryStore(string raw, out string error)
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            { error = Key + ": expected a whole number, got '" + raw + "'."; return false; }
            if (!InRange(v))
            { error = Key + ": " + I(v) + " is outside " + I(Min) + "-" + I(Max) + (VanillaSentinel ? " (or -1 for vanilla)" : "") + "."; return false; }
            if (_entry != null) _entry.Value = v;
            if (!Within(v)) Core.Log?.Msg("[Options] " + Key + " = " + I(v) + " is outside " + I(Min) + "-" + I(Max) + ", allowed by general.allowoutofrange.");
            error = null;
            return true;
        }

        private bool Within(int v) => (VanillaSentinel && v == -1) || (v >= Min && v <= Max);
        private bool InRange(int v) => Within(v) || Options.Unlimited;
    }

    public sealed class FloatOption : Option
    {
        public float Default { get; }
        public float Min { get; }
        public float Max { get; }
        public float Step { get; }

        /// <summary>-1 is accepted besides the range and means "the game's own value, read at runtime".</summary>
        public bool VanillaSentinel { get; }

        private MelonPreferences_Entry<float> _entry;

        public FloatOption(string group, string id, string label, string help, float def, float min, float max, float step,
            bool hostOnly = true, bool balance = true, bool vanillaSentinel = false)
            : base(group, id, label, help, hostOnly, balance)
        {
            Default = def; Min = min; Max = max; Step = step; VanillaSentinel = vanillaSentinel;
        }

        public override OptionKind Kind => OptionKind.Float;

        public float Value => Balance && Options.VanillaForced ? Default : Stored;

        internal float Stored
        {
            get
            {
                if (_entry == null) return Default;
                float v = _entry.Value;
                if (InRange(v)) return v;
                float c = float.IsNaN(v) ? Default : Math.Max(Min, Math.Min(Max, v));
                Core.WarnOnce("options: clamp " + Key, Key + " = " + F(v) + " is outside " + F(Min) + "-" + F(Max) + ", using " + F(c) + ".");
                return c;
            }
        }

        public override string DefaultText => F(Default);
        public override string ValueText => F(Stored);
        public override string RawText => _entry != null ? F(_entry.Value) : DefaultText;

        public override bool IsOutside(string raw)
            => float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && Finite(v) && !Within(v);

        public override string Describe(string raw)
        {
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return raw;
            return VanillaSentinel && v == -1f ? "Vanilla" : F(v);
        }

        internal override void Bind(MelonPreferences_Category category)
            => _entry = category.CreateEntry(Id, Default, Label, Help);

        protected override bool TryStore(string raw, out string error)
        {
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            { error = Key + ": expected a number like 1.5, got '" + raw + "'."; return false; }
            if (!Finite(v)) { error = Key + ": " + raw + " is not a usable number."; return false; }
            if (!InRange(v))
            { error = Key + ": " + F(v) + " is outside " + F(Min) + "-" + F(Max) + (VanillaSentinel ? " (or -1 for vanilla)" : "") + "."; return false; }
            if (_entry != null) _entry.Value = v;
            if (!Within(v)) Core.Log?.Msg("[Options] " + Key + " = " + F(v) + " is outside " + F(Min) + "-" + F(Max) + ", allowed by general.allowoutofrange.");
            error = null;
            return true;
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private bool Within(float v) => (VanillaSentinel && v == -1f) || (v >= Min - 1e-4f && v <= Max + 1e-4f);
        private bool InRange(float v) => Finite(v) && (Within(v) || Options.Unlimited);
    }

    public sealed class ChoiceOption : Option
    {
        public string Default { get; }
        public string[] Choices { get; }
        private MelonPreferences_Entry<string> _entry;

        public ChoiceOption(string group, string id, string label, string help, string def, string[] choices,
            bool hostOnly = true, bool balance = true)
            : base(group, id, label, help, hostOnly, balance)
        {
            Choices = choices;
            Default = def;
        }

        public override OptionKind Kind => OptionKind.Choice;

        public string Value => Balance && Options.VanillaForced ? Default : Stored;

        /// <summary>Position of <see cref="Value"/> in <see cref="Choices"/>.</summary>
        public int Index => Array.IndexOf(Choices, Value);

        internal string Stored
        {
            get
            {
                if (_entry == null) return Default;
                string match = Match(_entry.Value);
                if (match != null) return match;
                Core.WarnOnce("options: choice " + Key, Key + " = '" + _entry.Value + "' is not one of " + string.Join(", ", Choices) + ", using " + Default + ".");
                return Default;
            }
        }

        public override string DefaultText => Default;
        public override string ValueText => Stored;
        public override string Describe(string raw) => Match(raw) ?? raw;

        internal override void Bind(MelonPreferences_Category category)
            => _entry = category.CreateEntry(Id, Default, Label, Help);

        protected override bool TryStore(string raw, out string error)
        {
            string match = Match(raw);
            if (match == null) { error = Key + ": expected one of " + string.Join(", ", Choices) + ", got '" + raw + "'."; return false; }
            if (_entry != null) _entry.Value = match;
            error = null;
            return true;
        }

        private string Match(string raw)
        {
            foreach (string c in Choices)
                if (string.Equals(c, raw?.Trim(), StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }
    }
}
