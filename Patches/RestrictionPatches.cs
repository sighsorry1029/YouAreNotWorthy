using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

internal static class ItemRestriction
{
    internal const string InventorySlotsGuid = "sighsorry.InventorySlots";
    internal const string FineDiningGuid = "sighsorry.FineDining";
    private const float BlockedMessageCooldownSeconds = 1f;

    private static float _nextBlockedMessageTime;

    internal static bool TryBlock(
        Player player,
        ItemDrop.ItemData item,
        bool forceItemUse = false,
        bool showMessage = true)
    {
        try
        {
            if (!RestrictionEvaluator.TryGetMissingRequirement(
                    player,
                    item,
                    out CompiledItemTier requirement,
                    forceItemUse))
            {
                return false;
            }

            if (showMessage)
            {
                ShowBlockedMessage(
                    RequirementTextResolver.Resolve(requirement.RequiredKey));
            }

            return true;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
            return false;
        }
    }

    internal static bool ShowBlockedMessage(string? requirementText)
    {
        if ((Object?)Player.m_localPlayer == null)
        {
            return false;
        }

        float now = Time.time;
        if (now < _nextBlockedMessageTime)
        {
            return false;
        }

        _nextBlockedMessageTime = now + BlockedMessageCooldownSeconds;
        string message = RequirementTranslations.FormatBlocked();
        if (!string.IsNullOrWhiteSpace(requirementText))
        {
            message += "\n" + requirementText!.Trim();
        }

        Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
        return true;
    }
}

[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem), typeof(ItemDrop.ItemData), typeof(bool))]
internal static class HumanoidEquipItemRestrictionPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore(ItemRestriction.InventorySlotsGuid)]
    private static bool Prefix(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
    {
        if (__instance is not Player player
            || (Object?)player == null
            || item == null
            || !ItemRestriction.TryBlock(player, item))
        {
            return true;
        }

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.CanConsumeItem), typeof(ItemDrop.ItemData), typeof(bool))]
internal static class PlayerCanConsumeItemRestrictionPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Player __instance, ItemDrop.ItemData item, ref bool __result)
    {
        if (item == null || !ItemRestriction.TryBlock(__instance, item))
        {
            return true;
        }

        __result = false;
        return false;
    }
}

internal static class DoorKeyRestriction
{
    private delegate bool CanInteractDelegate(Door door);

    private static readonly CanInteractDelegate? CanInteract = CreateCanInteractDelegate();

    private static CanInteractDelegate? CreateCanInteractDelegate()
    {
        try
        {
            MethodInfo? method = AccessTools.DeclaredMethod(
                typeof(Door),
                "CanInteract",
                Type.EmptyTypes);
            if (method == null)
            {
                throw new MissingMethodException(typeof(Door).FullName, "CanInteract");
            }

            return AccessTools.MethodDelegate<CanInteractDelegate>(method, virtualCall: false);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Door key restrictions are disabled because Door.CanInteract could not be resolved: {ex}");
            return null;
        }
    }

    internal static bool TryGetKeyName(Door door, out string keyName)
    {
        keyName = string.Empty;
        if ((Object?)door.m_keyItem == null || door.m_keyItem.m_itemData?.m_shared == null)
        {
            return false;
        }

        keyName = door.m_keyItem.m_itemData.m_shared.m_name ?? string.Empty;
        return keyName.Length > 0;
    }

    internal static bool CanEvaluateUse(Door door)
    {
        return CanInteract != null
               && CanInteract(door)
               && (!door.m_checkGuardStone
                   || PrivateArea.CheckAccess(
                       door.transform.position,
                       0f,
                       flash: false));
    }
}

[HarmonyPatch(
    typeof(Door),
    nameof(Door.Interact),
    typeof(Humanoid),
    typeof(bool),
    typeof(bool))]
internal static class DoorInteractRestrictionPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(
        Door __instance,
        Humanoid character,
        bool hold,
        ref bool __result)
    {
        if (hold
            || character is not Player player
            || (Object?)player == null
            || (Object?)Player.m_localPlayer == null
            || (Object)player != (Object)Player.m_localPlayer
            || !DoorKeyRestriction.TryGetKeyName(__instance, out string keyName)
            || !DoorKeyRestriction.CanEvaluateUse(__instance))
        {
            return true;
        }

        ItemDrop.ItemData? keyItem = player.GetInventory().GetItem(keyName);
        if (keyItem == null
            || !ItemRestriction.TryBlock(player, keyItem, forceItemUse: true))
        {
            return true;
        }

        __result = true;
        return false;
    }
}

[HarmonyPatch(
    typeof(Door),
    nameof(Door.UseItem),
    typeof(Humanoid),
    typeof(ItemDrop.ItemData))]
internal static class DoorUseItemRestrictionPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(
        Door __instance,
        Humanoid user,
        ItemDrop.ItemData item,
        ref bool __result)
    {
        if (item?.m_shared == null
            || user is not Player player
            || (Object?)player == null
            || (Object?)Player.m_localPlayer == null
            || (Object)player != (Object)Player.m_localPlayer
            || !DoorKeyRestriction.TryGetKeyName(__instance, out string keyName)
            || !string.Equals(item.m_shared.m_name, keyName, StringComparison.Ordinal)
            || !DoorKeyRestriction.CanEvaluateUse(__instance)
            || !ItemRestriction.TryBlock(player, item, forceItemUse: true))
        {
            return true;
        }

        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(OfferingBowl), nameof(OfferingBowl.UseItem), typeof(Humanoid), typeof(ItemDrop.ItemData))]
internal static class OfferingBowlUseItemRestrictionPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(
        OfferingBowl __instance,
        Humanoid user,
        ItemDrop.ItemData item,
        ref bool __result)
    {
        if (__instance.m_useItemStands
            || (Object?)__instance.m_bossItem == null
            || item?.m_shared == null
            || user is not Player player
            || (Object?)player == null
            || !string.Equals(
                item.m_shared.m_name,
                __instance.m_bossItem.m_itemData.m_shared.m_name,
                StringComparison.Ordinal)
            || !ItemRestriction.TryBlock(player, item, forceItemUse: true))
        {
            return true;
        }

        __result = true;
        return false;
    }
}

[HarmonyPatch(
    typeof(OfferingBowl),
    "InitiateSpawnBoss",
    typeof(Vector3),
    typeof(bool))]
internal static class OfferingBowlItemStandRestrictionPatch
{
    private delegate List<ItemStand> FindItemStandsDelegate(OfferingBowl offeringBowl);

    private static readonly FindItemStandsDelegate? FindItemStands =
        CreateFindItemStandsDelegate();

    private static FindItemStandsDelegate? CreateFindItemStandsDelegate()
    {
        try
        {
            MethodInfo? method = AccessTools.DeclaredMethod(
                typeof(OfferingBowl),
                "FindItemStands",
                Type.EmptyTypes);
            if (method == null)
            {
                throw new MissingMethodException(typeof(OfferingBowl).FullName, "FindItemStands");
            }

            return AccessTools.MethodDelegate<FindItemStandsDelegate>(method, virtualCall: false);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Item-stand offering restrictions are disabled because OfferingBowl.FindItemStands could not be resolved: {ex}");
            return null;
        }
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(OfferingBowl __instance, Humanoid? ___m_interactUser)
    {
        if (!__instance.m_useItemStands
            || ___m_interactUser is not Player player
            || (Object?)player == null)
        {
            return true;
        }

        try
        {
            ObjectDB? objectDb = ObjectDB.instance;
            if ((Object?)objectDb == null || FindItemStands == null)
            {
                return true;
            }

            CompiledItemTier? highestMissingRequirement = null;
            foreach (ItemStand itemStand in FindItemStands(__instance))
            {
                if ((Object?)itemStand == null)
                {
                    continue;
                }

                string prefabName = itemStand.GetAttachedItem();
                if (string.IsNullOrWhiteSpace(prefabName))
                {
                    continue;
                }

                GameObject? prefab = objectDb.GetItemPrefab(prefabName);
                ItemDrop? itemDrop = prefab?.GetComponent<ItemDrop>();
                if ((Object?)itemDrop == null || itemDrop.m_itemData?.m_shared == null)
                {
                    YouAreNotWorthyPlugin.Log.LogWarning(
                        $"Could not resolve item-stand offering prefab '{prefabName}'; allowing that offering item.");
                    continue;
                }

                if (RestrictionEvaluator.TryGetMissingRequirement(
                        player,
                        itemDrop.m_itemData,
                        out CompiledItemTier requirement,
                        forceItemUse: true)
                    && (highestMissingRequirement == null
                        || requirement.Rank > highestMissingRequirement.Rank))
                {
                    highestMissingRequirement = requirement;
                }
            }

            if (highestMissingRequirement == null)
            {
                return true;
            }

            ItemRestriction.ShowBlockedMessage(
                RequirementTextResolver.Resolve(highestMissingRequirement.RequiredKey));
            return false;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(
                $"Failed to evaluate item-stand offering restrictions: {ex}");
            return true;
        }
    }
}

[HarmonyPatch(
    typeof(ItemDrop.ItemData),
    nameof(ItemDrop.ItemData.GetTooltip),
    typeof(ItemDrop.ItemData),
    typeof(int),
    typeof(bool),
    typeof(float),
    typeof(int))]
internal static class ItemTooltipRestrictionPatch
{
    [ThreadStatic]
    private static int _tooltipDepth;

    // ItemData.GetTooltip(int) wraps the static five-argument overload. Defer
    // the restriction suffix while that outer call is active so outer tooltip
    // extensions (notably FineDining's spoilage line) cannot split our final
    // warning from the end of the tooltip.
    [ThreadStatic]
    private static int _instanceTooltipDepth;

    [ThreadStatic]
    private static string? _deferredRestrictionSuffix;

    internal static bool BeginInstanceTooltip()
    {
        bool outermost = _instanceTooltipDepth++ == 0;
        if (outermost)
        {
            _deferredRestrictionSuffix = null;
        }

        return outermost;
    }

    internal static void EndInstanceTooltip(bool outermost)
    {
        _instanceTooltipDepth = Math.Max(0, _instanceTooltipDepth - 1);
        if (outermost || _instanceTooltipDepth == 0)
        {
            _deferredRestrictionSuffix = null;
        }
    }

    internal static void AppendDeferredRestriction(
        bool outermost,
        ref string tooltip)
    {
        if (!outermost || string.IsNullOrEmpty(_deferredRestrictionSuffix))
        {
            return;
        }

        tooltip += _deferredRestrictionSuffix;
        _deferredRestrictionSuffix = null;
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(out bool __state)
    {
        __state = _tooltipDepth++ == 0;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter(ItemRestriction.FineDiningGuid)]
    private static void Postfix(ItemDrop.ItemData item, bool __state, ref string __result)
    {
        try
        {
            if (!__state
                || item == null
                || (Object?)Player.m_localPlayer == null
                || !RestrictionEvaluator.TryGetMissingRequirement(
                    Player.m_localPlayer,
                    item,
                    out CompiledItemTier requirement))
            {
                return;
            }

            string blockedText = RequirementTranslations.FormatBlocked();
            string requirementText = RequirementTextResolver.Resolve(requirement.RequiredKey);
            string suffix = $"\n\n<color=#ff8080ff>{blockedText}\n{requirementText}</color>";
            if (_instanceTooltipDepth > 0)
            {
                _deferredRestrictionSuffix ??= suffix;
            }
            else
            {
                __result += suffix;
            }
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to append item restriction tooltip text: {ex}");
        }
    }

    [HarmonyFinalizer]
    [HarmonyPriority(Priority.Last)]
    private static Exception? Finalizer(Exception? __exception)
    {
        _tooltipDepth = Math.Max(0, _tooltipDepth - 1);
        return __exception;
    }
}

// Keep direct calls to the static overload supported above, but finish normal
// instance tooltip calls here after every FineDining outer-method postfix.
[HarmonyPatch(
    typeof(ItemDrop.ItemData),
    nameof(ItemDrop.ItemData.GetTooltip),
    typeof(int))]
internal static class ItemTooltipRestrictionOuterPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(out bool __state)
    {
        __state = ItemTooltipRestrictionPatch.BeginInstanceTooltip();
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter(ItemRestriction.FineDiningGuid)]
    private static void Postfix(bool __state, ref string __result)
    {
        ItemTooltipRestrictionPatch.AppendDeferredRestriction(
            __state,
            ref __result);
    }

    [HarmonyFinalizer]
    [HarmonyPriority(Priority.Last)]
    private static Exception? Finalizer(bool __state, Exception? __exception)
    {
        ItemTooltipRestrictionPatch.EndInstanceTooltip(__state);
        return __exception;
    }
}

[HarmonyPatch]
internal static class ItemTierCacheLifecyclePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodBase? objectDbRegisters = AccessTools.Method(
            typeof(ObjectDB),
            "UpdateRegisters",
            Type.EmptyTypes);
        MethodBase? netSceneAwake = AccessTools.Method(typeof(ZNetScene), "Awake");
        if (objectDbRegisters != null)
        {
            yield return objectDbRegisters;
        }

        if (netSceneAwake != null)
        {
            yield return netSceneAwake;
        }
    }

    private static void Postfix()
    {
        RestrictionEvaluator.InvalidateItemCache();
        RequirementTextResolver.InvalidateSources();
        ItemReferenceWriter.MarkDirty(ownerSourcesChanged: true);
        KeyReferenceWriter.MarkDirty();
    }
}

[HarmonyPatch]
internal static class InventorySlotsCanUseSpecialSlotRestrictionPatch
{
    private static PropertyInfo? _slotKindProperty;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        return Chainloader.PluginInfos.ContainsKey(ItemRestriction.InventorySlotsGuid);
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodBase? method = AccessTools.Method(
            "InventorySlots.InventorySlotsPlugin:CanUseSpecialSlot");
        if (method == null)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                "InventorySlots is installed, but CanUseSpecialSlot could not be resolved; that compatibility restriction is disabled.");
            yield break;
        }

        yield return method;
    }

    private static void Postfix(object[] __args, ref bool __result)
    {
        if (!__result
            || !TryGetNonQuickArguments(__args, out Player player, out ItemDrop.ItemData item)
            || !ItemRestriction.TryBlock(
                player,
                item,
                forceItemUse: true,
                showMessage: false))
        {
            return;
        }

        __result = false;
    }

    internal static bool TryGetNonQuickArguments(
        object[] args,
        out Player player,
        out ItemDrop.ItemData item)
    {
        player = null!;
        item = null!;
        if (args.Length < 4
            || args[0] is not Player candidatePlayer
            || args[2] is not ItemDrop.ItemData candidateItem
            || args[3] == null)
        {
            return false;
        }

        object slot = args[3];
        _slotKindProperty ??= AccessTools.Property(slot.GetType(), "Kind");
        object? kind = _slotKindProperty?.GetValue(slot, null);
        if (kind == null
            || string.Equals(kind.ToString(), "Quick", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        player = candidatePlayer;
        item = candidateItem;
        return true;
    }
}

[HarmonyPatch]
internal static class InventorySlotsTryEquipIntoSlotRestrictionPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        return Chainloader.PluginInfos.ContainsKey(ItemRestriction.InventorySlotsGuid);
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodBase? method = AccessTools.Method(
            "InventorySlots.InventorySlotsPlugin:TryEquipIntoSlot");
        if (method == null)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                "InventorySlots is installed, but TryEquipIntoSlot could not be resolved; that compatibility restriction is disabled.");
            yield break;
        }

        yield return method;
    }

    private static bool Prefix(object[] __args, ref bool __result)
    {
        if (!InventorySlotsCanUseSpecialSlotRestrictionPatch.TryGetNonQuickArguments(
                __args,
                out Player player,
                out ItemDrop.ItemData item)
            || !ItemRestriction.TryBlock(player, item, forceItemUse: true))
        {
            return true;
        }

        __result = false;
        return false;
    }
}
