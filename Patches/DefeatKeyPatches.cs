using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

[HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
internal static class Character_OnDeath_DefeatKey_Patch
{
    private static readonly MethodInfo QueueAdd = AccessTools.Method(typeof(List<string>), nameof(List<string>.Add));
    private static readonly MethodInfo QueuePersonalKey = AccessTools.Method(
        typeof(Character_OnDeath_DefeatKey_Patch),
        nameof(MaybeQueueVanillaDefeatKey));
    private static readonly MethodInfo SetGlobalKeyString = AccessTools.Method(
        typeof(ZoneSystem),
        nameof(ZoneSystem.SetGlobalKey),
        new[] { typeof(string) });
    private static readonly MethodInfo SetVanillaDefeatKey = AccessTools.Method(
        typeof(Character_OnDeath_DefeatKey_Patch),
        nameof(MaybeSetVanillaDefeatKey));
    private static readonly FieldInfo UniqueKeyQueue = AccessTools.Field(typeof(Player), nameof(Player.m_addUniqueKeyQueue));
    private static readonly FieldInfo DefeatKey = AccessTools.Field(typeof(Character), nameof(Character.m_defeatSetGlobalKey));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = instructions.Select(static instruction => new CodeInstruction(instruction)).ToList();
        List<int> queueMatches = new();
        for (int i = 0; i <= codes.Count - 4; i++)
        {
            if (codes[i].opcode == OpCodes.Ldsfld
                && Equals(codes[i].operand, UniqueKeyQueue)
                && codes[i + 1].opcode == OpCodes.Ldarg_0
                && codes[i + 2].opcode == OpCodes.Ldfld
                && Equals(codes[i + 2].operand, DefeatKey)
                && codes[i + 3].Calls(QueueAdd))
            {
                queueMatches.Add(i + 3);
            }
        }

        List<int> setMatches = new();
        for (int i = 0; i <= codes.Count - 3; i++)
        {
            if (codes[i].opcode == OpCodes.Ldarg_0
                && codes[i + 1].opcode == OpCodes.Ldfld
                && Equals(codes[i + 1].operand, DefeatKey)
                && codes[i + 2].Calls(SetGlobalKeyString))
            {
                setMatches.Add(i + 2);
            }
        }

        if (queueMatches.Count != 1 || setMatches.Count != 1)
        {
            throw new InvalidOperationException(
                "YNW expected one Vanilla defeat-key queue write and one world-key write "
                + $"in Character.OnDeath; found {queueMatches.Count} and {setMatches.Count}.");
        }

        CodeInstruction queueCall = codes[queueMatches[0]];
        queueCall.opcode = OpCodes.Call;
        queueCall.operand = QueuePersonalKey;

        CodeInstruction setCall = codes[setMatches[0]];
        setCall.opcode = OpCodes.Call;
        setCall.operand = SetVanillaDefeatKey;
        return codes;
    }

    private static void MaybeQueueVanillaDefeatKey(List<string> queue, string key)
    {
        if (!ProgressionIndex.TryRegisterPersonalKey(key, out _))
        {
            queue.Add(key);
        }
    }

    private static void MaybeSetVanillaDefeatKey(ZoneSystem zoneSystem, string key)
    {
        if (!ProgressionIndex.TryRegisterPersonalKey(key, out _))
        {
            zoneSystem.SetGlobalKey(key);
        }
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
internal static class DefeatKeyPatches
{
    private readonly struct DeathEvent
    {
        internal readonly bool IsValid;
        internal readonly Vector3 Position;
        internal readonly string PrefabName;
        internal readonly string DefeatKey;
        internal readonly ZNetView? RootNetworkView;

        internal DeathEvent(Character victim)
        {
            IsValid = true;
            Position = ((Component)victim).transform.position;
            PrefabName = ValheimNameUtils.GetPrefabName(((Component)victim).gameObject);
            DefeatKey = victim.m_defeatSetGlobalKey;
            RootNetworkView = ((Component)victim).GetComponent<ZNetView>();
        }
    }

    private static void Prefix(Character __instance, out DeathEvent __state)
    {
        __state = default;
        try
        {
            if (__instance is Player || !__instance.IsOwner())
            {
                return;
            }

            __state = new DeathEvent(__instance);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
        }
    }

    private static void Postfix(DeathEvent __state)
    {
        try
        {
            if (!__state.IsValid
                || ((Object?)__state.RootNetworkView != null && __state.RootNetworkView.GetZDO() != null))
            {
                return;
            }

            HandleDeath(__state);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
        }
    }

    private static void HandleDeath(DeathEvent deathEvent)
    {
        string? personalizedDefeatKey = null;
        if (ProgressionIndex.TryRegisterPersonalKey(deathEvent.DefeatKey, out string canonicalDefeatKey))
        {
            if (!PlayerKeys.TryRequestPersonalKeyAt(
                    canonicalDefeatKey,
                    deathEvent.Position,
                    originatedAsGlobalKey: true))
            {
                TrySetFallbackGlobalKey(canonicalDefeatKey);
            }

            personalizedDefeatKey = canonicalDefeatKey;
        }

        if (!ProgressionIndex.TryGetDefeatKeys(
                deathEvent.PrefabName,
                out IReadOnlyList<string> keys))
        {
            return;
        }

        foreach (string key in keys)
        {
            if (!string.IsNullOrEmpty(personalizedDefeatKey)
                && string.Equals(key, personalizedDefeatKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!PlayerKeys.TryRequestPersonalKeyAt(key, deathEvent.Position))
            {
                YouAreNotWorthyPlugin.Log.LogWarning(
                    $"Could not route custom defeat key '{key}' at its event position.");
            }
        }
    }

    private static void TrySetFallbackGlobalKey(string key)
    {
        try
        {
            if ((Object?)ZoneSystem.instance != null)
            {
                ZoneSystem.instance.SetGlobalKey(key);
            }
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(
                $"Failed to route fallback global-key request for '{key}': {ex}");
        }
    }
}
