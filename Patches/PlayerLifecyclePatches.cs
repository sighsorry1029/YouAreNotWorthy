using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

[HarmonyPatch(typeof(ZNet), "Awake")]
internal static class ZNet_RoutedRpcRegistration_Patch
{
    private static void Postfix()
    {
        PlayerKeys.EnsurePlayerRpcRegistered();
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
internal static class ZNet_DirectRpcRegistration_Patch
{
    private static void Postfix(ZNet __instance, ZNetPeer peer)
    {
        PlayerKeys.RegisterDirectPeer(__instance, peer);
        PlayerKeyCommands.RegisterPeer(__instance, peer);
        ItemReferenceCommands.RegisterPeer(__instance, peer);
    }
}

[HarmonyPatch]
internal static class PlayerLifecyclePatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.EquipInventoryItems))]
    private static void Player_EquipInventoryItems_Prefix(Player __instance)
    {
        try
        {
            if ((Object?)Player.m_localPlayer != null && (Object)__instance != (Object)Player.m_localPlayer)
            {
                return;
            }

            PlayerKeys.EnsurePlayerRpcRegistered();
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
        }
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter(ItemRestriction.InventorySlotsGuid)]
    [HarmonyPatch(typeof(Player), nameof(Player.EquipInventoryItems))]
    private static void Player_EquipInventoryItems_Postfix(Player __instance)
    {
        PublishPersonalKeys(__instance);
        RevalidateEquipment(__instance);
    }

    internal static void PublishPersonalKeys(Player __instance)
    {
        try
        {
            if ((Object?)Player.m_localPlayer != null && (Object)__instance == (Object)Player.m_localPlayer)
            {
                PersonalKeySnapshot.PublishLocal();
            }
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
        }
    }

    internal static void RevalidateEquipment(Player __instance)
    {
        try
        {
            RestrictionEvaluator.RevalidateEquipment(__instance);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
        }
    }
}

[HarmonyPatch]
internal static class PlayerPersonalKeySnapshotPatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Player), nameof(Player.Start));
        yield return AccessTools.Method(typeof(Player), nameof(Player.SetLocalPlayer));
        yield return AccessTools.Method(typeof(Player), nameof(Player.AddUniqueKey));
        yield return AccessTools.Method(typeof(Player), nameof(Player.RemoveUniqueKey));
        yield return AccessTools.Method(typeof(Player), nameof(Player.RemoveUniqueKeyValue));
        yield return AccessTools.Method(typeof(Player), nameof(Player.ResetUniqueKeys));
        yield return AccessTools.Method(typeof(Player), nameof(Player.ResetCharacter));
    }

    private static void Postfix(Player __instance)
    {
        PlayerLifecyclePatches.PublishPersonalKeys(__instance);
        PlayerLifecyclePatches.RevalidateEquipment(__instance);
    }
}
