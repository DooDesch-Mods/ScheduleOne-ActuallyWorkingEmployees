using System;
using System.Collections.Generic;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Growing;
using Il2CppScheduleOne.ObjectScripts;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Networking;
using ItemList = Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.EntityFramework.BuildableItem>;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.Balance
{
    /// <summary>
    /// Botanist option "use sprinklers": the botanist leaves a pot alone while a placed sprinkler that waters it has
    /// sprayed recently (see <see cref="Awe_WaterThreshold_Patch"/>). A vanilla sprinkler only sprays when a player
    /// triggers it (Sprinkler.Interacted, Sprinkler.cs:64), so a sprinkler nobody triggers must not hold the botanist
    /// back. A spray is seen through <c>Sprinkler.IsSprinkling</c> (Sprinkler.cs:46), which stays true for the whole
    /// spray, at least <c>ApplyWaterDelay + ParticleStopDelay</c> seconds, so the once-a-second sync cannot miss one.
    /// A sprinkler's pots are its <c>GetPots()</c>. The sprinkler list is rebuilt every few seconds while the option is
    /// on, because walking every placed item is too much for the once-a-second sync.
    /// </summary>
    internal sealed class SprinklerCoverage : BalanceFeature
    {
        public override string Name => "bal-sprinklers";

        private const float RebuildEvery = 5f;

        /// <summary>
        /// Game minutes a spray keeps its pots covered. A spray fills a pot (Sprinkler.cs:91-102, five fifths of the
        /// capacity); a pot loses a fifth of its capacity per hour (GrowContainer.cs:33,330) and the botanist waters
        /// below 30 percent, so a filled pot reaches that after 3.5 hours.
        /// </summary>
        private const int CoverMinutes = 180;

        private static readonly Dictionary<IntPtr, List<IntPtr>> _owners = new Dictionary<IntPtr, List<IntPtr>>();
        private static readonly List<Sprinkler> _list = new List<Sprinkler>();
        private static readonly Dictionary<IntPtr, int> _lastSpray = new Dictionary<IntPtr, int>();
        private static int _now = int.MinValue;
        private static int _sprinklers, _activePots;
        private static float _nextBuild;
        private static bool _loggedOff = true;

        protected override IEnumerable<Option> Watched => new Option[] { Options.BotanistUseSprinklers };

        protected override void OnInit()
        {
            Options.BotanistUseSprinklers.Changed += _ => _nextBuild = 0f;
            Eff.Add(Options.BotanistUseSprinklers, () => _sprinklers + " sprinkler(s) reach " + _owners.Count + " pot(s), " + _activePots + " left to them now");
#if DEBUG
            DevConsole.Register("sprinkle", "awe sprinkle  (the first sprinkler that reaches a pot sprays, as if a player used it)", _ => DevSprinkle());
#endif
        }

#if DEBUG
        private static void DevSprinkle()
        {
            if (!DevConsole.RequireHost("sprinkle")) return;
            if (!Options.BotanistUseSprinklers.Value) { Core.Log?.Warning("[Dev] [Sprinklers] turn on botanist.usesprinklers first; the sprinkler list is built only then."); return; }
            foreach (Sprinkler s in _list)
            {
                if (s == null) continue;
                int pots = 0;
                foreach (var kv in _owners) if (kv.Value.Contains(s.Pointer)) pots++;
                if (pots == 0) continue;
                if (!s.CanWater()) { Core.Log?.Warning("[Dev] [Sprinklers] " + s.name + " cannot spray now (it is spraying already)."); return; }
                s.SendWater();
                Core.Log?.Msg("[Dev] [Sprinklers] " + s.name + " sprays " + pots + " pot(s).");
                return;
            }
            Core.Log?.Warning("[Dev] [Sprinklers] no sprinkler reaches a pot (" + _sprinklers + " sprinkler(s) found).");
        }
#endif

        /// <summary>True when the botanist must leave this pot to a sprinkler that sprayed recently.</summary>
        internal static bool Refuses(GrowContainer container)
        {
            if (container == null || _owners.Count == 0) return false;
            if (!Net.IsServer || !Options.BotanistUseSprinklers.Value) return false;
            return _owners.TryGetValue(container.Pointer, out List<IntPtr> owners) && Covered(owners);
        }

        private static bool Covered(List<IntPtr> owners)
        {
            if (_now == int.MinValue) return false;
            for (int i = 0; i < owners.Count; i++)
                if (_lastSpray.TryGetValue(owners[i], out int at) && _now - at >= 0 && _now - at <= CoverMinutes) return true;
            return false;
        }

        protected override void Sync()
        {
            if (!Options.BotanistUseSprinklers.Value)
            {
                if (_owners.Count > 0 || !_loggedOff)
                {
                    ClearAll();
                    _loggedOff = true;
                    Core.Log?.Msg("[bal-sprinklers] option off, the botanist waters every dry pot.");
                }
                return;
            }
            _loggedOff = false;
            if (!ReadClock()) return;
            PollSprays();
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextBuild) { CountActive(); return; }
            _nextBuild = now + RebuildEvery;

            int beforePots = _owners.Count, beforeSprinklers = _sprinklers;
            _owners.Clear();
            _list.Clear();
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
            PollSprays();
            CountActive();
            if (beforePots != _owners.Count || beforeSprinklers != _sprinklers)
                Core.Log?.Msg("[bal-sprinklers] " + _sprinklers + " sprinkler(s) reach " + _owners.Count + " pot(s); the botanist leaves a pot alone for " + CoverMinutes + " game minutes after its sprinkler sprayed.");
        }

        /// <summary>Reads the game clock in minutes; false (and a warning) when the TimeManager is not there yet.</summary>
        private static bool ReadClock()
        {
            try
            {
                var tm = Il2CppScheduleOne.DevUtilities.NetworkSingleton<Il2CppScheduleOne.GameTime.TimeManager>.Instance;
                if (tm == null)
                {
                    Throttle.Warn("bal-sprinklers: clock", "TimeManager is not there yet; sprinkler cover is not evaluated and the botanist waters every pot.");
                    _now = int.MinValue;
                    return false;
                }
                _now = tm.GetTotalMinSum();
                return true;
            }
            catch (Exception ex) { Core.Failed("bal-sprinklers: read the game clock", ex); _now = int.MinValue; return false; }
        }

        /// <summary>Stamps every sprinkler that is spraying now with the current game minute.</summary>
        private static void PollSprays()
        {
            for (int i = 0; i < _list.Count; i++)
            {
                try
                {
                    Sprinkler s = _list[i];
                    if (s != null && s.IsSprinkling) _lastSpray[s.Pointer] = _now;
                }
                catch (Exception ex)
                {
                    Throttle.Warn("bal-sprinklers: poll", "Reading IsSprinkling failed (" + ex.GetType().Name + ": " + ex.Message + "); the sprinkler list is rebuilt now.");
                    _nextBuild = 0f;
                }
            }
        }

        private static void CountActive()
        {
            int n = 0;
            foreach (var kv in _owners) if (Covered(kv.Value)) n++;
            _activePots = n;
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
                _list.Add(sprinkler);
                try
                {
                    var pots = sprinkler.GetPots();
                    if (pots == null) { Throttle.Warn("bal-sprinklers: pots null", "A sprinkler returned no pot list; it covers nothing."); continue; }
                    for (int j = 0; j < pots.Count; j++)
                    {
                        Pot pot = pots[j];
                        if (pot == null) continue;
                        if (!_owners.TryGetValue(pot.Pointer, out List<IntPtr> owners)) _owners[pot.Pointer] = owners = new List<IntPtr>(1);
                        owners.Add(sprinkler.Pointer);
                    }
                }
                catch (Exception ex) { Core.Failed("bal-sprinklers: GetPots", ex); }
            }
        }

        private static void ClearAll()
        {
            _owners.Clear();
            _list.Clear();
            _lastSpray.Clear();
            _sprinklers = 0;
            _activePots = 0;
        }

        protected override void OnReset()
        {
            ClearAll();
            _now = int.MinValue;
            _nextBuild = 0f;
            _loggedOff = true;
        }

        protected override string Details() => "sprinklers=" + _sprinklers + " pots=" + _owners.Count + " leftToSprinklers=" + _activePots + " sprayed=" + _lastSpray.Count;
    }
}
