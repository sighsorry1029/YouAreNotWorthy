using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

internal static class RaidPersonalization
{
    internal static void RegisterEvents(RandEventSystem? eventSystem)
    {
        if ((Object?)eventSystem == null)
        {
            return;
        }

        foreach (RandomEvent randomEvent in eventSystem.m_events)
        {
            RegisterEventKeys(randomEvent);
        }
    }

    internal static void RefreshLocalEvents()
    {
        RegisterEvents(RandEventSystem.instance);
        if ((Object?)Player.m_localPlayer != null)
        {
            Player.m_localPlayer.UpdateEvents();
            PersonalKeySnapshot.PublishLocal();
        }
    }

    internal static void RegisterEventKeys(RandomEvent? randomEvent)
    {
        if (randomEvent == null)
        {
            return;
        }

        RegisterKeys(randomEvent.m_requiredGlobalKeys);
        RegisterKeys(randomEvent.m_notRequiredGlobalKeys);
        RegisterKeys(randomEvent.m_altRequiredPlayerKeysAny);
        RegisterKeys(randomEvent.m_altRequiredPlayerKeysAll);
        RegisterKeys(randomEvent.m_altNotRequiredPlayerKeys);
    }

    internal static bool TryEvaluate(Player? player, RandomEvent? randomEvent, out bool result)
    {
        result = false;
        if ((Object?)player == null || randomEvent == null)
        {
            return false;
        }

        RegisterEventKeys(randomEvent);
        if (UsesAltConditions(randomEvent) || !HasPersonalGlobalConditions(randomEvent))
        {
            return false;
        }

        if (!HaveSharedWorldConditions(randomEvent))
        {
            return true;
        }

        foreach (string key in randomEvent.m_requiredGlobalKeys ?? EmptyKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (ProgressionIndex.IsSharedWorldKey(key))
            {
                continue;
            }

            if (!ProgressionIndex.TryRegisterPersonalKey(key, out string canonicalKey)
                || !PlayerKeys.HasNativeKey(player, canonicalKey))
            {
                return true;
            }
        }

        foreach (string key in randomEvent.m_notRequiredGlobalKeys ?? EmptyKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (ProgressionIndex.IsSharedWorldKey(key))
            {
                continue;
            }

            if (ProgressionIndex.TryRegisterPersonalKey(key, out string canonicalKey)
                && PlayerKeys.HasNativeKey(player, canonicalKey))
            {
                return true;
            }
        }

        result = true;
        return true;
    }

    internal static bool UsesAltConditions(RandomEvent? randomEvent)
    {
        return randomEvent != null
               && IsPlayerEventsEnabled()
               && ((randomEvent.m_altRequiredKnownItems?.Count ?? 0) > 0
                   || (randomEvent.m_altRequiredNotKnownItems?.Count ?? 0) > 0
                   || (randomEvent.m_altRequiredPlayerKeysAny?.Count ?? 0) > 0
                   || (randomEvent.m_altRequiredPlayerKeysAll?.Count ?? 0) > 0
                   || (randomEvent.m_altNotRequiredPlayerKeys?.Count ?? 0) > 0);
    }

    internal static bool HasPersonalGlobalConditions(RandomEvent? randomEvent)
    {
        if (randomEvent == null)
        {
            return false;
        }

        foreach (string key in randomEvent.m_requiredGlobalKeys ?? EmptyKeys)
        {
            if (!string.IsNullOrWhiteSpace(key) && !ProgressionIndex.IsSharedWorldKey(key))
            {
                return true;
            }
        }

        foreach (string key in randomEvent.m_notRequiredGlobalKeys ?? EmptyKeys)
        {
            if (!string.IsNullOrWhiteSpace(key) && !ProgressionIndex.IsSharedWorldKey(key))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsPlayerEventsEnabled()
    {
        return ProgressionIndex.HasWorldKey(nameof(GlobalKeys.PlayerEvents));
    }

    internal static bool HasEligiblePlayerEvent(
        RandomEvent randomEvent,
        IEnumerable<RandEventSystem.PlayerEventData> players)
    {
        foreach (RandEventSystem.PlayerEventData player in players)
        {
            if (player.possibleEvents != null && player.possibleEvents.Contains(randomEvent.m_name))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasEligiblePlayerEventAt(
        RandomEvent randomEvent,
        IEnumerable<RandEventSystem.PlayerEventData> players,
        Vector3 position)
    {
        foreach (RandEventSystem.PlayerEventData player in players)
        {
            if (player.position.Equals(position)
                && player.possibleEvents != null
                && player.possibleEvents.Contains(randomEvent.m_name))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HaveSharedWorldConditions(RandomEvent? randomEvent)
    {
        if (randomEvent == null)
        {
            return false;
        }

        foreach (string key in randomEvent.m_requiredGlobalKeys ?? EmptyKeys)
        {
            if (!string.IsNullOrWhiteSpace(key)
                && ProgressionIndex.IsSharedWorldKey(key)
                && !ProgressionIndex.HasWorldKey(key))
            {
                return false;
            }
        }

        foreach (string key in randomEvent.m_notRequiredGlobalKeys ?? EmptyKeys)
        {
            if (!string.IsNullOrWhiteSpace(key)
                && ProgressionIndex.IsSharedWorldKey(key)
                && ProgressionIndex.HasWorldKey(key))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly List<string> EmptyKeys = new();

    private static void RegisterKeys(IEnumerable<string>? keys)
    {
        if (keys == null)
        {
            return;
        }

        foreach (string key in keys)
        {
            _ = ProgressionIndex.TryRegisterPersonalKey(key, out _);
        }
    }

}

[HarmonyPatch(typeof(RandEventSystem), "Awake")]
internal static class RandEventSystem_Awake_Patch
{
    private static void Postfix(RandEventSystem __instance)
    {
        try
        {
            RaidPersonalization.RegisterEvents(__instance);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
        }
    }
}

[HarmonyPatch(typeof(RandEventSystem), "Start")]
[HarmonyAfter("sighsorry.DropNSpawn")]
internal static class RandEventSystem_Start_Patch
{
    private static void Postfix()
    {
        try
        {
            RaidPersonalization.RefreshLocalEvents();
            KeyReferenceWriter.MarkDirty();
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
        }
    }
}

[HarmonyPatch(typeof(RandEventSystem), nameof(RandEventSystem.PlayerIsReadyForEvent))]
internal static class RandEventSystem_PlayerIsReadyForEvent_Patch
{
    private static bool Prefix(Player player, RandomEvent ev, ref bool __result)
    {
        try
        {
            if (!RaidPersonalization.TryEvaluate(player, ev, out bool result))
            {
                return true;
            }

            __result = result;
            return false;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(RandEventSystem), "HaveGlobalKeys")]
internal static class RandEventSystem_HaveGlobalKeys_Patch
{
    private static bool Prefix(RandomEvent ev, List<RandEventSystem.PlayerEventData> players, ref bool __result)
    {
        try
        {
            RaidPersonalization.RegisterEventKeys(ev);
            if (RaidPersonalization.UsesAltConditions(ev)
                || !RaidPersonalization.HasPersonalGlobalConditions(ev))
            {
                return true;
            }

            if (!RaidPersonalization.HaveSharedWorldConditions(ev))
            {
                __result = false;
                return false;
            }

            __result = RaidPersonalization.HasEligiblePlayerEvent(ev, players);
            return false;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(RandEventSystem), "GetValidEventPoints")]
internal static class RandEventSystem_GetValidEventPoints_Patch
{
    private static void Postfix(
        RandomEvent ev,
        List<RandEventSystem.PlayerEventData> characters,
        ref List<Vector3> __result)
    {
        try
        {
            if (RaidPersonalization.IsPlayerEventsEnabled()
                || !RaidPersonalization.HasPersonalGlobalConditions(ev))
            {
                return;
            }

            __result.RemoveAll(position =>
                !RaidPersonalization.HasEligiblePlayerEventAt(ev, characters, position));
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
            if (__result == null)
            {
                __result = new List<Vector3>();
            }
            else
            {
                __result.Clear();
            }
        }
    }
}
