using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;
using Object = UnityEngine.Object;

namespace JeepGlider;

// Adds the glider to the garage as a free roof cosmetic. The item is a copy of one of the game's
// own roof cosmetics with a new id, registered in the item catalog and the upgrade lists. Every
// car gets a hidden part object for it next to its other cosmetic parts, which the game's own
// mounting code switches on and off; Glider shows the glider while it's on.
static class RoofItem
{
    // Fixed so a saved purchase still matches next time; not used by any of the game's items
    internal const uint Id = 0x4A16611D;
    internal const string PartName = "JeepGlider_Roof";
    internal const string Title = "JeepGlider";
    internal const string Description = "A hang glider folded on the roof. Honk to open it and fly off the next ridge, honk again to fold it. " +
        "Steer to bank, throttle to dive, brake to pull up.";

    static VehicleCosmetic part;
    static bool registered;

    // Registers the item once the catalog has loaded. Returns whether it's registered.
    internal static bool EnsureRegistered()
    {
        if (registered) return true;
        var catalog = PlayerInventoryManager.InventoryItemsCatalog;
        if (catalog == null || catalog.IdToSO == null || catalog.IdToSO.Count == 0) return false;

        var template = PickTemplate(catalog);
        if (template == null) return false;
        if (catalog.IdToSO.ContainsKey(Id)) Mod.Log.Warning($"Roof item: id {Id} is already in the catalog; replacing it");

        part = Object.Instantiate(template);
        part.name = PartName;
        part.hideFlags = HideFlags.DontUnloadUnusedAsset;
        part.ItemId = Id;
        part.Price = 0;
        part.CosmeticType = CosmeticType.Roof;
        part.Icon = Icon();
        part.LinkedParts = new Il2CppSystem.Collections.Generic.List<VehiclePartSO>();
        part.Effects = new Il2CppSystem.Collections.Generic.List<UpgradeEffect>();

        catalog.IdToSO[Id] = part;
        if (catalog.IdToName != null) catalog.IdToName[Id] = PartName;
        if (catalog.NameToId != null) catalog.NameToId[PartName] = Id;
        // Every car that sells roof cosmetics, not just the ones selling the template
        int n = 0;
        foreach (var ul in Resources.FindObjectsOfTypeAll<UpgradesLists>())
        {
            if (ul.Parts == null || !HasRoofPart(ul)) continue;
            ul.Parts.Add(part);
            n++;
        }
        registered = true;
        Mod.Log.Msg($"Roof item: registered {PartName} (id {Id}, copied from {template.name}, in {n} upgrade lists)");
        return true;
    }

    static bool HasRoofPart(UpgradesLists ul)
    {
        foreach (var p in ul.Parts)
        {
            var c = p?.TryCast<VehicleCosmetic>();
            if (c != null && c.CosmeticType == CosmeticType.Roof && c.ItemId != Id) return true;
        }
        return false;
    }

    // A free, non-DLC roof cosmetic, preferring one with nothing linked to it
    static VehicleCosmetic PickTemplate(InventoryItemsCatalog catalog)
    {
        VehicleCosmetic best = null;
        foreach (var kv in catalog.IdToSO)
        {
            var c = kv.Value?.TryCast<VehicleCosmetic>();
            if (c == null || c.CosmeticType != CosmeticType.Roof) continue;
            if (c.name.Contains("CatalogExcluded") || (int)c.RequiredDlc != 0) continue;
            if (best == null || (best.LinkedParts != null && best.LinkedParts.Count > 0 && (c.LinkedParts == null || c.LinkedParts.Count == 0))) best = c;
        }
        return best;
    }

    // The part objects a car's customization controllers pick up and toggle
    internal static List<GameObject> PartObjects(Transform car)
    {
        var found = new List<GameObject>();
        if (!registered || car == null) return found;
        foreach (var u in car.GetComponentsInChildren<VehicleUpgradeObject>(true))
            if (Is(u.vehiclePartReference)) found.Add(u.gameObject);
        return found;
    }

    // Adds the part object next to the car's other roof parts, or any cosmetic part if it has no
    // roof parts. Call before the controllers collect their parts.
    internal static void AddPartObject(Transform car)
    {
        if (!EnsureRegistered() || car == null || PartObjects(car).Count > 0) return;
        Transform roof = null, any = null;
        foreach (var u in car.GetComponentsInChildren<VehicleUpgradeObject>(true))
        {
            var c = u.vehiclePartReference?.TryCast<VehicleCosmetic>();
            if (c == null || Is(c)) continue;
            any ??= u.transform.parent;
            if (c.CosmeticType == CosmeticType.Roof) { roof = u.transform.parent; break; }
        }
        var parent = roof ?? any;
        if (parent == null) return;
        var go = new GameObject(PartName);
        go.SetActive(false);
        go.transform.SetParent(parent, false);
        go.AddComponent<VehicleUpgradeObject>().vehiclePartReference = part;
        Mod.Log.Msg($"Roof item: part object added to {car.name}");
    }

    internal static bool Is(InventoryItemScriptableObject item) => item != null && item.ItemId == Id;

    // A delta wing seen from above, striped like the sail, on a transparent background
    static Sprite Icon()
    {
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = PartName, hideFlags = HideFlags.DontUnloadUnusedAsset };
        tex.SetPixels(IconPixels(n));
        tex.Apply();
        var s = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        s.name = PartName;
        s.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return s;
    }

    static Color[] IconPixels(int n)
    {
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n - 0.5f, v = (y + 0.5f) / n;
                // Nose at the top, trailing edge swept forward toward the middle
                float nose = 0.88f, trailing = 0.22f + 0.25f * (1f - Mathf.Abs(u) / 0.48f);
                float halfWidth = 0.48f * (nose - v) / (nose - 0.22f);
                float inside = Mathf.Min(halfWidth - Mathf.Abs(u), v - trailing);
                if (halfWidth <= 0f || inside <= 0f) continue;
                var c = Glider.Stripe(Mathf.Abs(u) / 0.48f);
                // Darker outline along the edges
                if (inside < 0.025f) c = Color.Lerp(c, Glider.Brown, 0.8f);
                c.a = Mathf.Clamp01(inside * n);
                px[y * n + x] = c;
            }
        return px;
    }
}

// The garage and every car collect their part objects here, so add ours first, then have Glider
// rig the car
[HarmonyPatch(typeof(LocalVehicleCustomizationController), nameof(LocalVehicleCustomizationController.InitParts))]
static class LocalInitPartsPatch
{
    static void Prefix(LocalVehicleCustomizationController __instance)
    {
        try
        {
            var root = __instance.transform.root;
            RoofItem.AddPartObject(root);
            Glider.CarReady(root);
        }
        catch (Exception e) { Mod.Log.Error(e); }
    }
}

[HarmonyPatch(typeof(VehicleCustomizationController), nameof(VehicleCustomizationController.InitVehicleUpgrades))]
static class NetInitPartsPatch
{
    static void Prefix(VehicleCustomizationController __instance)
    {
        try
        {
            var root = __instance.transform.root;
            RoofItem.AddPartObject(root);
            Glider.CarReady(root);
        }
        catch (Exception e) { Mod.Log.Error(e); }
    }
}

// Free and unlocked from the start, whatever unlocks the roof item it was copied from. Parts are
// owned per car, so once bought for this car it's no longer for sale and pressing it again removes it.
[HarmonyPatch]
static class AvailablePatch
{
    static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        foreach (var m in typeof(InventoryUtility).GetMethods())
            if (m.Name == nameof(InventoryUtility.IsAvailableToBuy)) yield return m;
    }

    static void Postfix(object[] __args, ref bool __result)
    {
        if (__result || __args.Length == 0 || __args[0] is not InventoryItemScriptableObject item || !RoofItem.Is(item)) return;
        __result = !Owned(item, __args);
    }

    // Asks the IsOwned overload matching the IsAvailableToBuy one that was called
    static bool Owned(InventoryItemScriptableObject item, object[] args) => args.Length < 2 ? InventoryUtility.IsOwned(item)
        : args[1] is CarInfo car ? InventoryUtility.IsOwned(item, car.ItemId)
        : args[1] is uint vehicle ? InventoryUtility.IsOwned(item, vehicle)
        : InventoryUtility.IsOwned(item);
}

// Its name and description, which have no entry in the game's localization tables
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.UpdateUpgradeDescription))]
static class DescriptionPatch
{
    static void Postfix(StateMerchantMenu __instance, UIMerchantButtonSelector button)
    {
        try
        {
            if (button == null || !__instance.buttonByInventoryItemSO.TryGetValue(button, out var item) || !RoofItem.Is(item)) return;
            var ui = __instance.merchantMenuUIContext;
            if (ui.DescriptionTitleText != null) ui.DescriptionTitleText.text = RoofItem.Title;
            if (ui.DescriptionText != null) ui.DescriptionText.text = RoofItem.Description;
        }
        catch (Exception e) { Mod.Log.Error(e); }
    }
}
