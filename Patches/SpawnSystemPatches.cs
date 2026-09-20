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
                "FindBaseSpawnPoint",
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

    internal static bool CanAttemptSpawnKey(ZoneSystem world, string key)
    {
        // 1.0 checks this before selecting a player. Personal keys are enforced
        // by FindEligibleBaseSpawnPoint; shared world keys keep the vanilla gate.
        return ProgressionIndex.TryRegisterPersonalKey(key, out _)
               || world.GetGlobalKey(key);
    }
}

[HarmonyPatch(typeof(SpawnSystem), "UpdateSpawnList",
    typeof(List<SpawnSystem.SpawnData>), typeof(DateTime), typeof(bool), typeof(string))]
internal static class SpawnSystem_UpdateSpawnList_Patch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo findBaseSpawnPoint = AccessTools.Method(
            typeof(SpawnSystem),
            "FindBaseSpawnPoint",
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
        MethodInfo canAttemptSpawnKey = AccessTools.Method(
            typeof(SpawnPersonalization),
            nameof(SpawnPersonalization.CanAttemptSpawnKey));
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
        int keyCallIndex = keyCalls[0];
        if (keyCallIndex >= findCallIndex)
        {
            throw new InvalidOperationException("YNW expected the 1.0 required-key check before spawn-point selection.");
        }

        codes[findCallIndex].opcode = OpCodes.Call;
        codes[findCallIndex].operand = findEligibleBaseSpawnPoint;

        codes[keyCallIndex].opcode = OpCodes.Call;
        codes[keyCallIndex].operand = canAttemptSpawnKey;
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
