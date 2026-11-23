using System;
using System.Reflection;
using ActuallyWorkingEmployees.Config;
using Il2CppScheduleOne.ItemFramework;   // ItemDefinition, StorableItemDefinition

namespace ActuallyWorkingEmployees.Features.Whip
{
    /// <summary>
    /// Puts the whip on the Gas-Mart shelf (every shop whose name contains "gas"). Shops are scene objects, so the listing is retried until they exist.
    ///
    /// <c>ShopIntegration.AddItemToShop</c> casts to S1API's older StorableItemDefinition wrapper while
    /// <c>ItemManager.GetDefinition</c> returns the newer one, so the plain path refuses mod items; the older wrapper is
    /// built through reflection (same as Yoink and BreedToSeed).
    /// </summary>
    internal static class WhipShop
    {
        private static bool _listed;
        private static bool _namesLogged;
        private static bool _offLogged;

        internal static void ResetSession() { _listed = false; _offLogged = false; }

        /// <summary>Idempotent; true once listed. Call from the Main scene until it reports true.</summary>
        internal static bool EnsureListed()
        {
            if (_listed) return true;
            if (!Options.WhipInShop.Value)
            {
                if (!_offLogged) { _offLogged = true; Core.Log?.Msg("[Whip] not listed in a shop: switched off by WhipInShop."); }
                return false;
            }
            if (!WhipItem.Available) return false;

            try
            {
                ItemDefinition native = Il2CppScheduleOne.Registry.GetItem(WhipItem.Id);
                if (native == null) return false;
                S1API.Items.ItemDefinition wrapped = WrapForShops(native);
                if (wrapped == null) return false;

                float price = Options.WhipPrice.Value;
                int shops = AddToGasMarts(wrapped, price, out bool anyShops);
                if (shops == 0) { if (anyShops) LogShopNamesOnce(); return false; }

                _listed = true;
                Core.Log?.Msg("[Whip] listed in " + shops + " shop(s) at $" + price + ".");
                return true;
            }
            catch (Exception e)
            {
                _listed = true;   // a broken build would otherwise retry every two seconds
                Core.Failed("whip: list in the shop", e);
                return false;
            }
        }

        /// <summary>Lists the whip in every shop whose name contains "gas" (the map has more than one Gas-Mart).</summary>
        private static int AddToGasMarts(S1API.Items.ItemDefinition item, float price, out bool anyShops)
        {
            anyShops = false;
            var all = S1API.Shops.ShopManager.GetAllShops();
            if (all == null || all.Length == 0) return 0;   // shops are scene objects; the caller retries
            anyShops = true;
            int taken = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var shop = all[i];
                if (shop == null || shop.Name == null || shop.Name.IndexOf("gas", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    // Shops outlive the session reset on save load, so the whip can already be on the shelf.
                    if (shop.HasItem(WhipItem.Id)) { taken++; Core.Log?.Msg("[Whip] already listed at '" + shop.Name + "'."); }
                    else if (shop.AddItem(item, price)) { taken++; Core.Log?.Msg("[Whip] listed at '" + shop.Name + "'."); }
                    else Core.Log?.Warning("[Whip] shop '" + shop.Name + "' refused the whip.");
                }
                catch (Exception e) { Core.Caught("whip: shop '" + shop.Name + "'", e); }
            }
            return taken;
        }

        private static S1API.Items.ItemDefinition WrapForShops(ItemDefinition native)
        {
            try
            {
                var storable = native.TryCast<StorableItemDefinition>();
                if (storable != null)
                {
                    Type wrapper = typeof(S1API.Items.ItemDefinition).Assembly.GetType("S1API.Items.StorableItemDefinition", false);
                    ConstructorInfo ctor = wrapper?.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                        null, new[] { typeof(StorableItemDefinition) }, null);
                    if (ctor != null && ctor.Invoke(new object[] { storable }) is S1API.Items.ItemDefinition built) return built;
                    Core.InfoOnce("whip legacy wrapper", "[Whip] S1API has no legacy StorableItemDefinition wrapper, using GetDefinition.");
                }
            }
            catch (Exception e) { Core.Caught("whip: legacy shop wrapper", e); }
            return S1API.Items.ItemManager.GetDefinition(WhipItem.Id);
        }

        private static void LogShopNamesOnce()
        {
            if (_namesLogged) return;
            _namesLogged = true;
            try
            {
                var all = S1API.Shops.ShopManager.GetAllShops();
                if (all == null || all.Length == 0) { Core.Log?.Msg("[Whip] no shops in the scene yet, retrying."); _namesLogged = false; return; }
                string names = "";
                for (int i = 0; i < all.Length; i++) if (all[i] != null) names += (names.Length > 0 ? ", " : "") + all[i].Name;
                Core.Log?.Warning("[Whip] could not place the whip in a shop. Shops present: " + names);
            }
            catch (Exception e) { Core.Caught("whip: list shop names", e); }
        }
    }
}
