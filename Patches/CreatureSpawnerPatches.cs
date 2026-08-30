using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

internal static class CreatureSpawnPersonalization
{
    internal static bool IsPlayerInRange(Vector3 point, float range, CreatureSpawner spawner)
    {
        if (!TryGetPersonalConditions(
                spawner,
                out bool requiredPersonal,
                out string requiredKey,
                out bool blockingPersonal,
                out string blockingKey))
        {
            return Player.IsPlayerInRange(point, range);
        }

        return HasEligibleTriggerPlayer(
            point,
            range,
            0f,
            false,
            requiredPersonal,
            requiredKey,
            blockingPersonal,
            blockingKey);
    }

    internal static bool IsPlayerInRange(Vector3 point, float range, float minNoise, CreatureSpawner spawner)
    {
        if (!TryGetPersonalConditions(
                spawner,
                out bool requiredPersonal,
                out string requiredKey,
                out bool blockingPersonal,
                out string blockingKey))
        {
            return Player.IsPlayerInRange(point, range, minNoise);
        }

        return HasEligibleTriggerPlayer(
            point,
            range,
            minNoise,
            true,
            requiredPersonal,
            requiredKey,
            blockingPersonal,
            blockingKey);
    }

    internal static bool HasEligibleTriggerPlayer(CreatureSpawner spawner)
    {
        if (!TryGetPersonalConditions(
                spawner,
                out bool requiredPersonal,
                out string requiredKey,
                out bool blockingPersonal,
                out string blockingKey))
        {
            return true;
        }

        return HasEligibleTriggerPlayer(
            ((Component)spawner).transform.position,
            spawner.m_triggerDistance,
            spawner.m_triggerNoise,
            spawner.m_triggerNoise > 0f,
            requiredPersonal,
            requiredKey,
            blockingPersonal,
            blockingKey);
    }

    internal static bool IsBlockedBySharedWorldConditions(CreatureSpawner spawner)
    {
        _ = TryGetPersonalConditions(
            spawner,
            out bool requiredPersonal,
            out _,
            out bool blockingPersonal,
            out _);

        return (!requiredPersonal
                && !string.IsNullOrWhiteSpace(spawner.m_requiredGlobalKey)
                && !ProgressionIndex.HasWorldKey(spawner.m_requiredGlobalKey))
               || (!blockingPersonal
                   && !string.IsNullOrWhiteSpace(spawner.m_blockingGlobalKey)
                   && ProgressionIndex.HasWorldKey(spawner.m_blockingGlobalKey));
    }

    internal static bool HasPersonalConditions(CreatureSpawner spawner)
    {
        return TryGetPersonalConditions(spawner, out _, out _, out _, out _);
    }

    private static bool HasEligibleTriggerPlayer(
        Vector3 point,
        float range,
        float minNoise,
        bool requireNoise,
        bool requiredPersonal,
        string requiredKey,
        bool blockingPersonal,
        string blockingKey)
    {
        HashSet<ZDOID> seenCharacters = new();
        foreach (Player player in Player.GetAllPlayers())
        {
            if ((Object?)player == null)
            {
                continue;
            }

            ZDOID characterId = player.GetZDOID();
            if (!characterId.IsNone())
            {
                seenCharacters.Add(characterId);
            }

            if (!MeetsTrigger(player, point, range, minNoise, requireNoise)
                || !HasPersonalKeys(
                    player,
                    requiredPersonal,
                    requiredKey,
                    blockingPersonal,
                    blockingKey))
            {
                continue;
            }

            return true;
        }

        if ((Object?)ZNet.instance == null)
        {
            return false;
        }

        foreach (ZDO characterZdo in ZNet.instance.GetAllCharacterZDOS())
        {
            if (characterZdo == null || !seenCharacters.Add(characterZdo.m_uid))
            {
                continue;
            }

            if (!MeetsTrigger(characterZdo, point, range, minNoise, requireNoise)
                || !HasPersonalKeys(
                    characterZdo,
                    requiredPersonal,
                    requiredKey,
                    blockingPersonal,
                    blockingKey))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool MeetsTrigger(Player player, Vector3 point, float range, float minNoise, bool requireNoise)
    {
        if (Vector3.Distance(((Component)player).transform.position, point) >= range)
        {
            return false;
        }

        if (!requireNoise)
        {
            return true;
        }

        float noiseRange = player.GetNoiseRange();
        return range <= noiseRange && noiseRange >= minNoise;
    }

    private static bool MeetsTrigger(ZDO characterZdo, Vector3 point, float range, float minNoise, bool requireNoise)
    {
        if (Vector3.Distance(characterZdo.GetPosition(), point) >= range)
        {
            return false;
        }

        if (!requireNoise)
        {
            return true;
        }

        float noiseRange = characterZdo.GetFloat(ZDOVars.s_noise, 0f);
        return range <= noiseRange && noiseRange >= minNoise;
    }

    private static bool HasPersonalKeys(
        Player player,
        bool requiredPersonal,
        string requiredKey,
        bool blockingPersonal,
        string blockingKey)
    {
        if (requiredPersonal
            && (!PersonalKeySnapshot.TryHas(player, requiredKey, out bool hasRequired) || !hasRequired))
        {
            return false;
        }

        if (blockingPersonal
            && (!PersonalKeySnapshot.TryHas(player, blockingKey, out bool hasBlocking) || hasBlocking))
        {
            return false;
        }

        return true;
    }

    private static bool HasPersonalKeys(
        ZDO characterZdo,
        bool requiredPersonal,
        string requiredKey,
        bool blockingPersonal,
        string blockingKey)
    {
        if (requiredPersonal
            && (!PersonalKeySnapshot.TryHas(characterZdo, requiredKey, out bool hasRequired) || !hasRequired))
        {
            return false;
        }

        if (blockingPersonal
            && (!PersonalKeySnapshot.TryHas(characterZdo, blockingKey, out bool hasBlocking) || hasBlocking))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetPersonalConditions(
        CreatureSpawner spawner,
        out bool requiredPersonal,
        out string requiredKey,
        out bool blockingPersonal,
        out string blockingKey)
    {
        requiredKey = string.Empty;
        blockingKey = string.Empty;
        requiredPersonal = ProgressionIndex.TryRegisterPersonalKey(spawner.m_requiredGlobalKey, out requiredKey);
        blockingPersonal = ProgressionIndex.TryRegisterPersonalKey(spawner.m_blockingGlobalKey, out blockingKey);
        return requiredPersonal || blockingPersonal;
    }
}

[HarmonyPatch(typeof(CreatureSpawner), nameof(CreatureSpawner.UpdateSpawner))]
internal static class CreatureSpawner_UpdateSpawner_Patch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo rangeCheck = AccessTools.Method(
            typeof(Player),
            nameof(Player.IsPlayerInRange),
            new[] { typeof(Vector3), typeof(float) });
        MethodInfo noiseCheck = AccessTools.Method(
            typeof(Player),
            nameof(Player.IsPlayerInRange),
            new[] { typeof(Vector3), typeof(float), typeof(float) });
        MethodInfo personalRangeCheck = AccessTools.Method(
            typeof(CreatureSpawnPersonalization),
            nameof(CreatureSpawnPersonalization.IsPlayerInRange),
            new[] { typeof(Vector3), typeof(float), typeof(CreatureSpawner) });
        MethodInfo personalNoiseCheck = AccessTools.Method(
            typeof(CreatureSpawnPersonalization),
            nameof(CreatureSpawnPersonalization.IsPlayerInRange),
            new[] { typeof(Vector3), typeof(float), typeof(float), typeof(CreatureSpawner) });

        List<CodeInstruction> codes = instructions.Select(static instruction => new CodeInstruction(instruction)).ToList();
        List<(int Index, MethodInfo Replacement)> replacements = new();
        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].Calls(rangeCheck))
            {
                replacements.Add((i, personalRangeCheck));
            }
            else if (codes[i].Calls(noiseCheck))
            {
                replacements.Add((i, personalNoiseCheck));
            }
        }

        if (replacements.Count(entry => entry.Replacement == personalRangeCheck) != 1
            || replacements.Count(entry => entry.Replacement == personalNoiseCheck) != 1)
        {
            throw new InvalidOperationException("YNW found unexpected CreatureSpawner.UpdateSpawner trigger checks.");
        }

        foreach ((int index, MethodInfo replacement) in replacements.OrderByDescending(static entry => entry.Index))
        {
            if (codes[index].blocks.Count != 0)
            {
                throw new InvalidOperationException("YNW found an unexpected exception boundary on a CreatureSpawner trigger check.");
            }

            CodeInstruction loadSpawner = new(OpCodes.Ldarg_0);
            loadSpawner.labels.AddRange(codes[index].labels);
            codes[index].labels.Clear();
            codes.Insert(index, loadSpawner);
            codes[index + 1].opcode = OpCodes.Call;
            codes[index + 1].operand = replacement;
        }

        return codes;
    }
}

[HarmonyPatch(typeof(CreatureSpawner), nameof(CreatureSpawner.CheckGlobalKeys))]
internal static class CreatureSpawner_CheckGlobalKeys_Patch
{
    private static bool Prefix(CreatureSpawner __instance, ref bool __result)
    {
        try
        {
            if (!CreatureSpawnPersonalization.HasPersonalConditions(__instance))
            {
                return true;
            }

            __result = CreatureSpawnPersonalization.IsBlockedBySharedWorldConditions(__instance)
                       || !CreatureSpawnPersonalization.HasEligibleTriggerPlayer(__instance);
            return false;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
            __result = true;
            return false;
        }
    }
}

[HarmonyPatch(typeof(CreatureSpawner), nameof(CreatureSpawner.Spawn))]
internal static class CreatureSpawner_Spawn_Patch
{
    private static bool Prefix(CreatureSpawner __instance, ref ZNetView __result)
    {
        try
        {
            if (!CreatureSpawnPersonalization.HasPersonalConditions(__instance))
            {
                return true;
            }

            if (!CreatureSpawnPersonalization.IsBlockedBySharedWorldConditions(__instance)
                && CreatureSpawnPersonalization.HasEligibleTriggerPlayer(__instance))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(ex);
        }

        __result = null!;
        return false;
    }
}
