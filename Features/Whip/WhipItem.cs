using System;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Host;
using Il2CppScheduleOne.ItemFramework;   // ItemInstance
using Il2CppScheduleOne.PlayerScripts;   // PlayerInventory

namespace ActuallyWorkingEmployees.Features.Whip
{
    /// <summary>
    /// The whip as an item (AWE#63): its definition through S1API, its model built in code (<see cref="WhipModel"/>) and
    /// its icon rendered from that model. Registered on every load before the save data, so a whip in a saved inventory finds its
    /// definition, and again from the tick when that callback came too early.
    /// </summary>
    internal static class WhipItem
    {
        internal const string Id = "awe_whip";

        private static bool _registered;
        private static GameObject _modelSource;
        private static bool _modelFailed;
        private static Sprite _icon;

        internal static bool Available => _registered;
        internal static Sprite IconSprite => _icon;

        /// <summary>Placement of the model in the hand container of the first-person avatar. Tuned live with 'awe whip vm'.</summary>
        internal static Vector3 HeldPosition = new Vector3(0.0276f, -0.2191f, -0.0973f);   // grip on the hand bone of the machete arm (awe whip vm grip 0)
        internal static Vector3 HeldRotation = new Vector3(334.0f, 348.6f, 10.0f);   // handle 25 degrees up and 20 degrees towards the screen middle (awe whip vm aim 25 -20)
        internal static float HeldScale = 1f;
        /// <summary>Shift and turn of the whole first-person arm on top of the machete's placement. Tuned live with 'awe whip vm arm'.</summary>
        internal static Vector3 ArmOffset = new Vector3(-0.02f, 0.01f, 0.02f);   // fist left of the corner and further forward
        internal static Vector3 ArmRotation = new Vector3(-15f, 0f, 0f);       // forearm tipped up, so the fist shows instead of the wrist

        /// <summary>
        /// Load start or scene change: the next tick checks the registry again. The held template stays; it is rebuilt only
        /// when the definition has to be created anew, because the old one no longer points at it.
        /// </summary>
        internal static void MarkUnchecked() { _registered = false; }

        /// <summary>Idempotent per load. Never throws; a failure is logged and the next call tries again.</summary>
        internal static void EnsureRegistered()
        {
            if (_registered) return;
            try
            {
                if (S1API.Items.ItemManager.GetDefinition(Id) != null)
                {
                    _registered = true;
                    Core.Log?.Msg("[Whip] item '" + Id + "' already registered.");
                    return;
                }
            }
            catch (Exception e) { Core.Caught("whip: look up the registered item", e); }

            try
            {
                WhipEquippable.ResetSession();   // a new definition: the held template has to be pointed at it again
                var equippable = S1API.Items.Storable.ItemCreator.CreateEquippableBuilder()
                    .CreateViewmodelEquippable("AWEWhipEquippable")
                    .WithViewmodelTransform(position: new Vector3(0.18f, -0.16f, 0.34f), rotation: new Vector3(0f, 200f, 0f), scale: Vector3.one)
                    .WithInteraction(canInteract: true, canPickup: true)
                    .WithAvatarEquippable(
                        assetPath: S1API.Items.AvatarEquippablePaths.M1911,
                        hand: S1API.Items.AvatarHand.Right,
                        animationTrigger: "RightArm_Hold_ClosedHand")
                    .Build();

                var builder = S1API.Items.Storable.ItemCreator.CreateBuilder()
                    .WithBasicInfo(Id, Loc.Text("Whip"),
                        Loc.Text("Crack it at an employee. A working employee works faster for a while, an idle or stuck one restarts its task."),
                        S1API.Items.ItemCategory.Tools)
                    .WithStackLimit(1)
                    .WithPricing(Options.WhipPrice.Value, 0.4f)
                    .WithLegalStatus(S1API.Items.LegalStatus.Legal)
                    .WithIcon(Icon())
                    .WithEquippable(equippable);
                // A StoredItem with footprint tiles, so a storage can draw the whip (AWE#68); WhipStoredItem logs when it cannot.
                GameObject stored = WhipStoredItem.Ensure();
                if (stored != null) builder = builder.WithStoredItem(stored);
                builder.Build();

                _registered = true;
                Core.Log?.Msg("[Whip] item '" + Id + "' registered at $" + Options.WhipPrice.Value + ".");
            }
            catch (Exception e) { Core.Failed("whip: register the item", e); }
        }

        /// <summary>The whip model built in code, inactive and kept across scenes. Null when it cannot be built; the whip then has no model in hand.</summary>
        internal static GameObject EnsureModelSource()
        {
            if (_modelSource != null) return _modelSource;
            if (_modelFailed) return null;
            try
            {
                _modelSource = WhipModel.Build("AWEWhipModel", false);
                _modelSource.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_modelSource);
                Core.Log?.Msg("[Whip] whip model built.");
                return _modelSource;
            }
            catch (Exception e)
            {
                _modelFailed = true;
                Core.Failed("whip: load the model", e);
                return null;
            }
        }

        /// <summary>True while the local player holds the whip.</summary>
        internal static bool IsEquipped()
        {
            try
            {
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                if (inv == null || !inv.isAnythingEquipped) return false;
                ItemInstance item = inv.equippedSlot != null ? inv.equippedSlot.ItemInstance : null;
                return item != null && item.Definition != null && item.Definition.ID == Id;
            }
            catch (Exception e) { Core.Caught("whip: read the equipped slot", e); return false; }
        }

        /// <summary>The whip with its lash laid out in a curve, inactive. The caller destroys it after rendering.</summary>
        internal static GameObject BuildIconModel()
        {
            GameObject go = WhipModel.Build("AWEWhipIconModel", true);
            go.SetActive(false);
            return go;
        }

        private static Sprite Icon()
        {
            if (_icon != null) return _icon;
            GameObject model = null;
            try
            {
                model = BuildIconModel();
                _icon = IconRenderer.Render(model);
            }
            catch (Exception e) { Core.Caught("whip: render the icon", e); }
            finally
            {
                try { if (model != null) UnityEngine.Object.Destroy(model); }
                catch (Exception e) { Core.Caught("whip: destroy the icon model", e); }
            }
            if (_icon != null) return _icon;
            _icon = BorrowedIcon();
            return _icon;
        }

        /// <summary>Only when the render failed: a vanilla tool sprite instead of an empty square.</summary>
        private static Sprite BorrowedIcon()
        {
            string[] donors = { "trimmers", "trashgrabber", "wateringcan" };
            for (int i = 0; i < donors.Length; i++)
            {
                try
                {
                    var def = Il2CppScheduleOne.Registry.GetItem(donors[i]);
                    if (def != null && def.Icon != null)
                    {
                        Core.Log?.Warning("[Whip] icon render failed, using the icon of '" + donors[i] + "'.");
                        return def.Icon;
                    }
                }
                catch (Exception e) { Core.Caught("whip: borrow icon " + donors[i], e); }
            }
            Core.Log?.Warning("[Whip] no icon for the whip, the slot stays empty.");
            return null;
        }
    }
}
