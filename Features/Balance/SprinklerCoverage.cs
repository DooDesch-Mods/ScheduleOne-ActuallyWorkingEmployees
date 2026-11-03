using System;
using System.Collections.Generic;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Growing;
using Il2CppScheduleOne.ObjectScripts;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Networking;
using ItemList = Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.EntityFramework.BuildableItem>;

namespace ActuallyWorkingEmployees.Features.Balance
{
    /// <summary>
    /// Botanist option "use sprinklers": the pots a placed sprinkler waters are refused by the botanist's watering check
    /// (see <see cref="Awe_WaterThreshold_Patch"/>). A sprinkler's pots are its <c>GetPots()</c>, the pots on the tiles it
    /// sprays. The set is rebuilt every few seconds while the option is on, because walking every placed item is too
    /// much for the once-a-second sync.
    /// </summary>
    internal sealed class SprinklerCoverage : BalanceFeature
    {
        public override string Name => "bal-sprinklers";

        private const float RebuildEvery = 5f;
        private static readonly HashSet<IntPtr> _covered = new HashSet<IntPtr>();
        private static int _sprinklers;
        private static float _nextBuild;
        private static bool _loggedOff = true;

        protected override IEnumerable<Option> Watched => new Option[] { Options.BotanistUseSprinklers };

        protected override void OnInit()
        {
            Options.BotanistUseSprinklers.Changed += _ => _nextBuild = 0f;
            Eff.Add(Options.BotanistUseSprinklers, () => _sprinklers + " sprinkler(s) cover " + _covered.Count + " pot(s)");
        }

        /// <summary>True when the botanist must leave this pot to a sprinkler.</summary>
        internal static bool Refuses(GrowContainer container)
        {
            if (container == null || _covered.Count == 0) return false;
            if (!Net.IsServer || !Options.BotanistUseSprinklers.Value) return false;
            return _covered.Contains(container.Pointer);
        }

        protected override void Sync()
        {
            if (!Options.BotanistUseSprinklers.Value)
            {
                if (_covered.Count > 0 || !_loggedOff)
                {
                    _covered.Clear();
                    _sprinklers = 0;
                    _loggedOff = true;
                    Core.Log?.Msg("[bal-sprinklers] option off, the botanist waters every dry pot.");
                }
                return;
            }
            _loggedOff = false;
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextBuild) return;
            _nextBuild = now + RebuildEvery;

            int beforePots = _covered.Count, beforeSprinklers = _sprinklers;
            _covered.Clear();
            _sprinklers = 0;
            var properties = Il2CppScheduleOne.Property.Property.Properties;
            if (properties == null) Throttle.Warn("bal-sprinklers: properties", "Property.Properties is null; no sprinkler is found.");
            else
                for (int i = 0; i < properties.Count; i++)
                {
                    Il2CppScheduleOne.Property.Property p = properties[i];
                    if (p == null) continue;
                    ItemList items = p.BuildableItems;
                    if (items == null) { Throttle.Warn("bal-sprinklers: items " + i, "Property " + p.name + " has no BuildableItems list."); continue; }
                    Collect(items);
                }
            var businesses = Il2CppScheduleOne.Property.Business.Businesses;
            if (businesses == null) Throttle.Warn("bal-sprinklers: businesses", "Business.Businesses is null; no business sprinkler is found.");
            else
                for (int i = 0; i < businesses.Count; i++)
                {
                    Il2CppScheduleOne.Property.Business b = businesses[i];
                    if (b == null) continue;
                    ItemList items = b.BuildableItems;
                    if (items == null) { Throttle.Warn("bal-sprinklers: business items " + i, "Business " + b.name + " has no BuildableItems list."); continue; }
                    Collect(items);
                }
            if (beforePots != _covered.Count || beforeSprinklers != _sprinklers)
                Core.Log?.Msg("[bal-sprinklers] " + _sprinklers + " sprinkler(s) cover " + _covered.Count + " pot(s); the botanist leaves them to the sprinklers.");
        }

        private static void Collect(ItemList items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                BuildableItem item = items[i];
                if (item == null) continue;
                Sprinkler sprinkler = item.TryCast<Sprinkler>();
                if (sprinkler == null || sprinkler.isGhost) continue;
                _sprinklers++;
                try
                {
                    var pots = sprinkler.GetPots();
                    if (pots == null) continue;
                    for (int j = 0; j < pots.Count; j++)
                    {
                        Pot pot = pots[j];
                        if (pot != null) _covered.Add(pot.Pointer);
                    }
                }
                catch (Exception ex) { Core.Failed("bal-sprinklers: GetPots", ex); }
            }
        }

        protected override void OnReset()
        {
            _covered.Clear();
            _sprinklers = 0;
            _nextBuild = 0f;
            _loggedOff = true;
        }

        protected override string Details() => "sprinklers=" + _sprinklers + " pots=" + _covered.Count;
    }
}
