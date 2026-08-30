using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace YouAreNotWorthy;

internal static class SpawnPersonalization
{
    private delegate bool FindBaseSpawnPointDelegate(
        SpawnSystem system,
        SpawnSystem.SpawnData spawn,
        List<Player> players,
        out Vector3 spawnCenter,
        out Player targetPlayer);

    private static readonly FindBaseSpawnPointDelegate FindBaseSpawnPoint =
        AccessTools.MethodDelegate<FindBaseSpawnPointDelegate>(
            AccessTools.DeclaredMethod(
                typeof(SpawnSystem),
                nameof(SpawnSystem.FindBaseSpawnPoint),
                new[]
                {
                    typeof(SpawnSystem.SpawnData),
                    typeof(List<Player>),
                    typeof(Vector3).MakeByRefType(),
                    typeof(Player).MakeByRefType()
                }),
            instance: null,
            virtualCall: false);

    internal static bool FindEligibleBaseSpawnPoint(
        SpawnSystem system,
        SpawnSystem.SpawnData spawn,
        List<Player> players,
        out Vector3 spawnCenter,
        out Player targetPlayer)
    {
        if (!ProgressionIndex.TryRegisterPersonalKey(spawn.m_requiredGlobalKey, out string canonicalKey))
        {
            return FindBaseSpawnPoint(
                system,
                spawn,
                players,
                out spawnCenter,
                out targetPlayer);
        }

        List<Player> eligiblePlayers = new();
        foreach (Player player in players)
        {
            if (PersonalKeySnapshot.TryHas(player, canonicalKey, out bool hasKey) && hasKey)
            {
                eligiblePlayers.Add(player);
            }
        }

        if (eligiblePlayers.Count == 0)
        {
            spawnCenter = Vector3.zero;
            targetPlayer = null!;
            return false;
        }

        return FindBaseSpawnPoint(
            system,
            spawn,
            eligiblePlayers,
            out spawnCenter,
            out targetPlayer);
    }

    internal static bool HasRequiredSpawnKey(ZoneSystem world, string key, Player targetPlayer)
    {
        if (!ProgressionIndex.TryRegisterPersonalKey(key, out string canonicalKey))
        {
            return world.GetGlobalKey(key);
        }

        return PersonalKeySnapshot.TryHas(targetPlayer, canonicalKey, out bool hasKey) && hasKey;
    }
}

[HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.UpdateSpawnList))]
internal static class SpawnSystem_UpdateSpawnList_Patch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo findBaseSpawnPoint = AccessTools.Method(
            typeof(SpawnSystem),
            nameof(SpawnSystem.FindBaseSpawnPoint),
            new[]
            {
                typeof(SpawnSystem.SpawnData),
                typeof(List<Player>),
                typeof(Vector3).MakeByRefType(),
                typeof(Player).MakeByRefType()
            });
        MethodInfo findEligibleBaseSpawnPoint = AccessTools.Method(
            typeof(SpawnPersonalization),
            nameof(SpawnPersonalization.FindEligibleBaseSpawnPoint));
        MethodInfo getGlobalKey = AccessTools.Method(
            typeof(ZoneSystem),
            nameof(ZoneSystem.GetGlobalKey),
            new[] { typeof(string) });
        MethodInfo hasRequiredSpawnKey = AccessTools.Method(
            typeof(SpawnPersonalization),
            nameof(SpawnPersonalization.HasRequiredSpawnKey));
        FieldInfo requiredGlobalKey = AccessTools.Field(
            typeof(SpawnSystem.SpawnData),
            nameof(SpawnSystem.SpawnData.m_requiredGlobalKey));

        List<CodeInstruction> codes = instructions.Select(static instruction => new CodeInstruction(instruction)).ToList();
        List<int> findCalls = FindInstructionIndexes(codes, instruction => instruction.Calls(findBaseSpawnPoint));
        List<int> keyCalls = new();
        for (int i = 1; i < codes.Count; i++)
        {
            if (codes[i].Calls(getGlobalKey)
                && codes[i - 1].opcode == OpCodes.Ldfld
                && Equals(codes[i - 1].operand, requiredGlobalKey))
            {
                keyCalls.Add(i);
            }
        }

        if (findCalls.Count != 1 || keyCalls.Count != 1)
        {
            throw new InvalidOperationException(
                $"YNW expected one FindBaseSpawnPoint and one m_requiredGlobalKey check in SpawnSystem.UpdateSpawnList; found {findCalls.Count} and {keyCalls.Count}.");
        }

        int findCallIndex = findCalls[0];
        int targetAddressIndex = findCallIndex - 1;
        if (targetAddressIndex < 0
            || (codes[targetAddressIndex].opcode != OpCodes.Ldloca
                && codes[targetAddressIndex].opcode != OpCodes.Ldloca_S))
        {
            throw new InvalidOperationException("YNW could not capture SpawnSystem.UpdateSpawnList's selected target local.");
        }

        int keyCallIndex = keyCalls[0];
        if (codes[keyCallIndex].blocks.Count != 0)
        {
            throw new InvalidOperationException("YNW found an unexpected required-key check in SpawnSystem.UpdateSpawnList.");
        }

        object targetLocal = codes[targetAddressIndex].operand;
        if (targetLocal is LocalBuilder local && local.LocalType != typeof(Player))
        {
            throw new InvalidOperationException("YNW captured a non-Player target local in SpawnSystem.UpdateSpawnList.");
        }

        codes[findCallIndex].opcode = OpCodes.Call;
        codes[findCallIndex].operand = findEligibleBaseSpawnPoint;

        CodeInstruction loadTarget = new(
            codes[targetAddressIndex].opcode == OpCodes.Ldloca_S ? OpCodes.Ldloc_S : OpCodes.Ldloc,
            targetLocal);
        loadTarget.labels.AddRange(codes[keyCallIndex].labels);
        codes[keyCallIndex].labels.Clear();
        codes.Insert(keyCallIndex, loadTarget);
        keyCallIndex++;
        codes[keyCallIndex].opcode = OpCodes.Call;
        codes[keyCallIndex].operand = hasRequiredSpawnKey;
        return codes;
    }

    private static List<int> FindInstructionIndexes(
        IReadOnlyList<CodeInstruction> codes,
        Func<CodeInstruction, bool> predicate)
    {
        List<int> indexes = new();
        for (int i = 0; i < codes.Count; i++)
        {
            if (predicate(codes[i]))
            {
                indexes.Add(i);
            }
        }

        return indexes;
    }
}
