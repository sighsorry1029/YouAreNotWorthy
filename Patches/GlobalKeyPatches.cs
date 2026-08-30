using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

[HarmonyPatch(typeof(ZoneSystem), "Awake")]
internal static class ZoneSystem_Awake_KeyReference_Patch
{
    private static void Prefix()
    {
        KeyReferenceWriter.Reset();
    }
}

[HarmonyPatch(typeof(ZoneSystem), "Start")]
internal static class ZoneSystem_Start_KeyReference_Patch
{
    private static void Postfix()
    {
        KeyReferenceWriter.MarkRuntimeReady();
    }
}

[HarmonyPatch]
internal static class ZoneSystem_GetGlobalKey_Patches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGlobalKey), typeof(string))]
    private static bool StringPrefix(string name, ref bool __result)
    {
        return Evaluate(name, ref __result);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGlobalKey), typeof(GlobalKeys))]
    private static bool GlobalKeysPrefix(GlobalKeys key, ref bool __result)
    {
        return Evaluate(key.ToString(), ref __result);
    }

    private static bool Evaluate(string keyName, ref bool result)
    {
        try
        {
            KeyReferenceWriter.ObserveGlobalKey(keyName);
            if (!ProgressionIndex.TryRegisterPersonalKey(keyName, out _))
            {
                return true;
            }

            result = PlayerKeys.Has(keyName);
            return false;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.RPC_SetGlobalKey))]
internal static class ZoneSystem_RPC_SetGlobalKey_Patch
{
    private static bool Prefix(long sender, string name)
    {
        KeyReferenceWriter.ObserveGlobalKey(name);
        if (!ProgressionIndex.TryRegisterPersonalKey(name, out string canonicalKey))
        {
            return true;
        }

        try
        {
            if (!PlayerKeys.TryDistributeGlobalKey(canonicalKey, sender))
            {
                YouAreNotWorthyPlugin.Log.LogWarning(
                    $"Blocked world write for personal key '{canonicalKey}', but no source player could be resolved.");
            }
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
        }

        return false;
    }
}

[HarmonyPatch]
internal static class Interaction_GlobalKey_EventPosition_Patch
{
    private static readonly MethodInfo SetGlobalKeyString = AccessTools.Method(
        typeof(ZoneSystem),
        nameof(ZoneSystem.SetGlobalKey),
        new[] { typeof(string) });
    private static readonly MethodInfo SetGlobalKeyAtEventPosition = AccessTools.Method(
        typeof(Interaction_GlobalKey_EventPosition_Patch),
        nameof(MaybeSetGlobalKeyAtEventPosition));

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(
            typeof(Trader),
            nameof(Trader.UseItem),
            new[] { typeof(Humanoid), typeof(ItemDrop.ItemData) });
        yield return AccessTools.Method(
            typeof(OfferingBowl),
            nameof(OfferingBowl.UseItem),
            new[] { typeof(Humanoid), typeof(ItemDrop.ItemData) });
        yield return AccessTools.Method(
            typeof(Vegvisir),
            nameof(Vegvisir.Interact),
            new[] { typeof(Humanoid), typeof(bool), typeof(bool) });
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        List<CodeInstruction> codes = instructions.Select(static instruction => new CodeInstruction(instruction)).ToList();
        List<int> matches = new();
        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].Calls(SetGlobalKeyString))
            {
                matches.Add(i);
            }
        }

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"YNW expected one global-key write in {original.DeclaringType?.Name}.{original.Name}; found {matches.Count}.");
        }

        CodeInstruction call = codes[matches[0]];
        CodeInstruction loadEventSource = new(OpCodes.Ldarg_0);
        loadEventSource.labels.AddRange(call.labels);
        call.labels.Clear();
        loadEventSource.blocks.AddRange(call.blocks);
        call.blocks.Clear();
        call.opcode = OpCodes.Call;
        call.operand = SetGlobalKeyAtEventPosition;
        codes.Insert(matches[0], loadEventSource);
        return codes;
    }

    private static void MaybeSetGlobalKeyAtEventPosition(
        ZoneSystem zoneSystem,
        string key,
        Component eventSource)
    {
        KeyReferenceWriter.ObserveGlobalKey(key);
        if ((Object?)eventSource != null
            && PlayerKeys.TryRequestPersonalKeyAt(
                key,
                eventSource.transform.position,
                originatedAsGlobalKey: true))
        {
            return;
        }

        zoneSystem.SetGlobalKey(key);
    }
}

[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.RPC_GlobalKeys))]
internal static class ZoneSystem_RPC_GlobalKeys_Patch
{
    private static void Postfix()
    {
        try
        {
            if ((Object?)Player.m_localPlayer != null)
            {
                Player.m_localPlayer.UpdateEvents();
            }
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
        }
    }
}
