using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

internal static class PersonalKeySnapshot
{
    private const string SnapshotZdoKey = "YNW_PersonalKeys";
    private const int SnapshotMagic = 0x31574E59;
    private const byte SnapshotVersion = 1;
    private const int MaxSnapshotKeys = 4096;
    private const int MaxSnapshotBytes = 1024 * 1024;

    internal static void PublishLocal()
    {
        Player? player = Player.m_localPlayer;
        if ((Object?)player == null
            || !player.IsOwner()
            || !TryGetPlayerZdo(player, out ZDO zdo))
        {
            return;
        }

        List<string> keys = player.GetUniqueKeys();
        keys.RemoveAll(static key => string.IsNullOrWhiteSpace(key));
        keys.Sort(StringComparer.Ordinal);
        if (keys.Count > MaxSnapshotKeys)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Personal-key snapshot contains {keys.Count} keys; only the first {MaxSnapshotKeys} will be published.");
            keys.RemoveRange(MaxSnapshotKeys, keys.Count - MaxSnapshotKeys);
        }

        byte[] payload = Serialize(keys, out int publishedCount);
        if (publishedCount < keys.Count)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Personal-key snapshot exceeded {MaxSnapshotBytes} bytes; "
                + $"only the first {publishedCount} of {keys.Count} keys will be published.");
        }

        byte[] current = zdo.GetByteArray(SnapshotZdoKey, Array.Empty<byte>());
        if (!ByteArraysEqual(current, payload))
        {
            zdo.Set(SnapshotZdoKey, payload);
        }
    }

    internal static bool TryHas(Player? player, string? key, out bool hasKey)
    {
        hasKey = false;
        if ((Object?)player == null
            || !ProgressionIndex.TryGetCanonicalPersonalKey(key, out string canonicalKey))
        {
            return false;
        }

        if ((Object?)Player.m_localPlayer != null && (Object)player == (Object)Player.m_localPlayer)
        {
            hasKey = PlayerKeys.HasNativeKey(player, canonicalKey);
            return true;
        }

        if (!TryGetPlayerZdo(player, out ZDO zdo))
        {
            return false;
        }

        return TryHas(zdo, canonicalKey, out hasKey);
    }

    internal static bool TryHas(ZDO? zdo, string? key, out bool hasKey)
    {
        hasKey = false;
        if (zdo == null
            || !ProgressionIndex.TryGetCanonicalPersonalKey(key, out string canonicalKey))
        {
            return false;
        }

        return TryHasLiteral(zdo, canonicalKey, out hasKey);
    }

    internal static bool TryHasLiteral(ZDO? zdo, string? key, out bool hasKey)
    {
        hasKey = false;
        string literal = (key ?? string.Empty).Trim();
        if (zdo == null || literal.Length == 0)
        {
            return false;
        }

        byte[] payload = zdo.GetByteArray(SnapshotZdoKey, Array.Empty<byte>());
        return TryRead(payload, literal, out hasKey);
    }

    private static byte[] Serialize(List<string> keys, out int publishedCount)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true))
        {
            writer.Write(SnapshotMagic);
            writer.Write(SnapshotVersion);
            long countPosition = stream.Position;
            writer.Write(0);
            publishedCount = 0;
            foreach (string key in keys)
            {
                long keyPosition = stream.Position;
                writer.Write(key);
                writer.Flush();
                if (stream.Length > MaxSnapshotBytes)
                {
                    stream.SetLength(keyPosition);
                    stream.Position = keyPosition;
                    break;
                }

                publishedCount++;
            }

            long endPosition = stream.Position;
            stream.Position = countPosition;
            writer.Write(publishedCount);
            stream.Position = endPosition;
        }

        return stream.ToArray();
    }

    private static bool TryGetPlayerZdo(Player player, out ZDO zdo)
    {
        zdo = null!;
        ZDOID characterId = player.GetZDOID();
        ZDOMan? zdoMan = ZDOMan.instance;
        if (characterId.IsNone() || zdoMan == null)
        {
            return false;
        }

        zdo = zdoMan.GetZDO(characterId);
        return zdo != null;
    }

    private static bool TryRead(byte[] payload, string canonicalKey, out bool hasKey)
    {
        hasKey = false;
        if (payload.Length == 0 || payload.Length > MaxSnapshotBytes)
        {
            return false;
        }

        try
        {
            using MemoryStream stream = new(payload, false);
            using BinaryReader reader = new(stream, Encoding.UTF8, false);
            if (reader.ReadInt32() != SnapshotMagic || reader.ReadByte() != SnapshotVersion)
            {
                return false;
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > MaxSnapshotKeys)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                if (string.Equals(reader.ReadString(), canonicalKey, StringComparison.OrdinalIgnoreCase))
                {
                    hasKey = true;
                }
            }

            return stream.Position == stream.Length;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ByteArraysEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}
