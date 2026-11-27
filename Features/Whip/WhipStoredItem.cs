using System;
using Il2CppScheduleOne.ItemFramework;   // StorableItemDefinition
using Il2CppScheduleOne.Storage;         // StoredItem
using Il2CppScheduleOne.Tiles;           // FootprintTile

namespace ActuallyWorkingEmployees.Features.Whip
{
    /// <summary>
    /// The whip as it lies in a storage (AWE#68).
    ///
    /// Every storage with a visualizer redraws its contents when a slot changes, and reads the size of each item's
    /// StoredItem for that. Without one of our own, S1API gives the whip a bare StoredItem with no footprint tiles, so the
    /// redraw throws inside ItemSlot.SetStoredItem. The drop then stops halfway: the item is already in the storage
    /// slot, but the line that takes it out of the inventory never runs. While the whip stays in the storage, every
    /// later drop into it throws the same way.
    ///
    /// The template is a copy of a vanilla tool's StoredItem, which keeps its footprint tiles and build point, with the
    /// tool's model swapped for the whip lying coiled on the tool's spot. Kept across scenes like the held model.
    /// </summary>
    internal static class WhipStoredItem
    {
        /// <summary>Vanilla tools whose StoredItem lies flat on a shelf; the first one with footprint tiles is copied.</summary>
        private static readonly string[] Donors = { "machete", "baseballbat", "fryingpan", "trimmers" };

        private static GameObject _bank;
        private static GameObject _template;

        /// <summary>The StoredItem prefab for the whip definition, or null when no vanilla tool can be copied yet.</summary>
        internal static GameObject Ensure()
        {
            if (_template != null) return _template;
            for (int i = 0; i < Donors.Length; i++)
            {
                try
                {
                    var def = Il2CppScheduleOne.Registry.GetItem(Donors[i])?.TryCast<StorableItemDefinition>();
                    StoredItem donor = def != null ? def.StoredItem : null;
                    if (donor == null) { Core.Log?.Msg("[Whip] stored model: '" + Donors[i] + "' has no StoredItem, trying the next tool."); continue; }
                    var pairs = donor._coordinateFootprintTilePairs;
                    if (pairs == null || pairs.Count == 0) { Core.Log?.Msg("[Whip] stored model: '" + Donors[i] + "' has no footprint tiles, trying the next tool."); continue; }
                    _template = Build(donor, Donors[i]);
                    if (_template != null) return _template;
                }
                catch (Exception e) { Core.Caught("whip: stored model from " + Donors[i], e); }
            }
            Core.Log?.Warning("[Whip] no vanilla tool to copy the stored model from; putting the whip into a storage leaves it in the inventory too.");
            return null;
        }

        private static GameObject Build(StoredItem donor, string donorId)
        {
            if (_bank == null)
            {
                _bank = new GameObject("AWEStoredItemTemplates");
                _bank.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_bank);
            }
            GameObject go = UnityEngine.Object.Instantiate(donor.gameObject, _bank.transform);
            go.name = "AWEWhipStoredItem";
            Transform root = go.transform;

            // The tool's model: where it lies and how much room it takes. Footprint tiles are markers, not model.
            Bounds spot = default;
            bool hasSpot = false;
            foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null || mf.GetComponent<FootprintTile>() != null) continue;
                Encapsulate(ref spot, ref hasSpot, root.worldToLocalMatrix * mf.transform.localToWorldMatrix, mf.sharedMesh.bounds);
            }
            int removed = 0;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.GetComponent<FootprintTile>() != null) continue;
                r.enabled = false;
                UnityEngine.Object.Destroy(r);
                removed++;
            }

            GameObject whip = WhipModel.Build("AWEWhipStoredModel", true);
            Transform wt = whip.transform;
            wt.SetParent(root, false);
            Bounds own = default;
            bool hasOwn = false;
            foreach (MeshFilter mf in whip.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                Encapsulate(ref own, ref hasOwn, wt.worldToLocalMatrix * mf.transform.localToWorldMatrix, mf.sharedMesh.bounds);
            }

            float yaw = 0f, scale = 1f;
            if (hasSpot && hasOwn && own.size.x > 0f && own.size.z > 0f)
            {
                // Turn the coiled whip so its long side runs along the tool's, then shrink it only when it is larger.
                float straight = Mathf.Min(spot.size.x / own.size.x, spot.size.z / own.size.z);
                float turned = Mathf.Min(spot.size.x / own.size.z, spot.size.z / own.size.x);
                if (turned > straight) yaw = 90f;
                scale = Mathf.Min(1f, Mathf.Max(straight, turned));
            }
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
            wt.localRotation = rot;
            wt.localScale = Vector3.one * scale;
            if (hasOwn)
            {
                Vector3 centre = rot * (own.center * scale);
                Vector3 target = hasSpot ? spot.center : Vector3.zero;
                float floor = hasSpot ? spot.min.y : 0f;
                wt.localPosition = new Vector3(target.x - centre.x, floor - own.min.y * scale, target.z - centre.z);
            }
            whip.SetActive(true);

            Core.Log?.Msg("[Whip] stored model built from '" + donorId + "': " + removed + " tool renderer(s) replaced, whip scale "
                + scale.ToString("F2") + ", turned " + yaw.ToString("F0") + " degrees" + (hasSpot ? "" : ", no tool model to line up with") + ".");
            return go;
        }

        private static void Encapsulate(ref Bounds into, ref bool any, Matrix4x4 toLocal, Bounds b)
        {
            Vector3 min = b.min, max = b.max;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
                Vector3 p = toLocal.MultiplyPoint3x4(corner);
                if (!any) { into = new Bounds(p, Vector3.zero); any = true; }
                else into.Encapsulate(p);
            }
        }
    }
}
