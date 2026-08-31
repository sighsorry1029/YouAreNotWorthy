using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace YouAreNotWorthy;

internal static class LocationIconIdentityTransport
{
    internal readonly struct ReceivedLocationIdentity
    {
        internal ReceivedLocationIdentity(string prefabName, string displayToken)
        {
            PrefabName = prefabName;
            DisplayToken = displayToken;
        }

        internal string PrefabName { get; }
        internal string DisplayToken { get; }
    }

    internal sealed class ReceivedIdentitySnapshot
    {
        internal Dictionary<Vector3, ReceivedLocationIdentity> Identities { get; } = new();
    }

    private const string MetadataMarker = "\u001fYNWLOC1:";
    private const int MaxLocationIcons = 100000;
    private const int MaxPrefabNameBytes = 1024;
    private const float HorizontalPositionTolerance = 0.0001f;
    private const float VerticalPositionTolerance = 0.00002f;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static ZoneSystem? _receivedOwner;
    private static IReadOnlyDictionary<Vector3, ReceivedLocationIdentity> _receivedIdentities =
        new Dictionary<Vector3, ReceivedLocationIdentity>();

    internal static void Clear()
    {
        _receivedOwner = null;
        _receivedIdentities = new Dictionary<Vector3, ReceivedLocationIdentity>();
    }

    internal static bool TryGetLocationName(
        ZoneSystem zoneSystem,
        Vector3 position,
        string displayToken,
        out string locationName)
    {
        if ((UnityEngine.Object?)ZNet.instance != null && ZNet.instance.IsServer())
        {
            return TryGetServerLocationName(zoneSystem, position, out locationName);
        }

        if (ReferenceEquals(_receivedOwner, zoneSystem)
            && _receivedIdentities.TryGetValue(
                position,
                out ReceivedLocationIdentity identity)
            && string.Equals(identity.DisplayToken, displayToken, StringComparison.Ordinal))
        {
            locationName = identity.PrefabName;
            return true;
        }

        locationName = string.Empty;
        return false;
    }

    internal static void PopulateTransportIcons(
        ZoneSystem zoneSystem,
        Dictionary<Vector3, string> icons)
    {
        zoneSystem.GetLocationIcons(icons);
        foreach (Vector3 position in icons.Keys.ToList())
        {
            if (!TryGetServerLocationName(zoneSystem, position, out string prefabName)
                || string.IsNullOrEmpty(icons[position]))
            {
                continue;
            }

            icons[position] = Encode(icons[position], prefabName);
        }
    }

    internal static bool IsTrustedSender(long sender)
    {
        ZNet? net = ZNet.instance;
        if ((UnityEngine.Object?)net == null)
        {
            return false;
        }

        if (net.IsServer())
        {
            return true;
        }

        ZNetPeer? serverPeer = net.GetServerPeer();
        return serverPeer != null
               && serverPeer.m_server
               && serverPeer.m_uid == sender;
    }

    internal static ReceivedIdentitySnapshot RewriteReceivedPackage(ref ZPackage package)
    {
        int initialPosition = package.GetPos();
        try
        {
            int count = package.ReadInt();
            if (count < 0 || count > MaxLocationIcons)
            {
                throw new InvalidDataException($"Invalid location-icon count {count}.");
            }

            ZPackage cleanPackage = new();
            cleanPackage.Write(count);
            ReceivedIdentitySnapshot snapshot = new();
            bool containedMetadata = false;
            for (int index = 0; index < count; index++)
            {
                Vector3 position = package.ReadVector3();
                string transportValue = package.ReadString();
                bool hasEnvelope;
                if (TryDecode(
                        transportValue,
                        out string displayToken,
                        out string prefabName,
                        out hasEnvelope))
                {
                    snapshot.Identities[position] = new ReceivedLocationIdentity(
                        prefabName,
                        displayToken);
                }

                if (hasEnvelope)
                {
                    containedMetadata = true;
                    transportValue = displayToken;
                }

                cleanPackage.Write(position);
                cleanPackage.Write(transportValue);
            }

            if (!containedMetadata)
            {
                package.SetPos(initialPosition);
                return new ReceivedIdentitySnapshot();
            }

            package = AppendUnreadPayload(cleanPackage, package);
            return snapshot;
        }
        catch (Exception ex)
        {
            package.SetPos(initialPosition);
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Could not decode YNW location-icon identities; using the original packet: {ex.Message}");
            return new ReceivedIdentitySnapshot();
        }
    }

    internal static void CommitReceivedLocationNames(
        ZoneSystem owner,
        ReceivedIdentitySnapshot snapshot)
    {
        _receivedOwner = owner;
        _receivedIdentities = snapshot.Identities;
    }

    private static bool TryGetServerLocationName(
        ZoneSystem zoneSystem,
        Vector3 position,
        out string locationName)
    {
        Vector2i zone = ZoneSystem.GetZone(position);
        if (zoneSystem.m_locationInstances.TryGetValue(
                zone,
                out ZoneSystem.LocationInstance instance))
        {
            Vector3 instancePosition = instance.m_position;
            if (Mathf.Abs(position.x - instancePosition.x) > HorizontalPositionTolerance
                || Mathf.Abs(position.z - instancePosition.z) > HorizontalPositionTolerance
                || Mathf.Abs(position.y - instancePosition.y) > VerticalPositionTolerance)
            {
                locationName = string.Empty;
                return false;
            }

            ZoneSystem.ZoneLocation? location = instance.m_location;
            string prefabName = location?.m_prefabName?.Trim() ?? string.Empty;
            if (prefabName.Length > 0)
            {
                locationName = prefabName;
                return true;
            }
        }

        locationName = string.Empty;
        return false;
    }

    private static string Encode(string displayToken, string prefabName)
    {
        if (displayToken.IndexOf(MetadataMarker, StringComparison.Ordinal) >= 0)
        {
            return displayToken;
        }

        try
        {
            byte[] prefabBytes = StrictUtf8.GetBytes(prefabName);
            if (prefabBytes.Length == 0 || prefabBytes.Length > MaxPrefabNameBytes)
            {
                return displayToken;
            }

            return displayToken + MetadataMarker + Convert.ToBase64String(prefabBytes);
        }
        catch (EncoderFallbackException)
        {
            return displayToken;
        }
    }

    private static bool TryDecode(
        string value,
        out string displayToken,
        out string prefabName,
        out bool hasEnvelope)
    {
        displayToken = value;
        prefabName = string.Empty;
        hasEnvelope = false;
        int markerIndex = value.IndexOf(MetadataMarker, StringComparison.Ordinal);
        if (markerIndex <= 0)
        {
            return false;
        }

        displayToken = value.Substring(0, markerIndex);
        hasEnvelope = true;
        if (value.IndexOf(
                MetadataMarker,
                markerIndex + MetadataMarker.Length,
                StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        string encodedPrefab = value.Substring(markerIndex + MetadataMarker.Length);
        if (encodedPrefab.Length == 0 || encodedPrefab.Length > MaxPrefabNameBytes * 2)
        {
            return false;
        }

        try
        {
            byte[] prefabBytes = Convert.FromBase64String(encodedPrefab);
            if (prefabBytes.Length == 0 || prefabBytes.Length > MaxPrefabNameBytes)
            {
                return false;
            }

            string decodedPrefab = StrictUtf8.GetString(prefabBytes).Trim();
            if (ValheimNameUtils.NormalizePrefabName(decodedPrefab).Length == 0)
            {
                return false;
            }

            prefabName = decodedPrefab;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static ZPackage AppendUnreadPayload(ZPackage cleanPackage, ZPackage originalPackage)
    {
        byte[] cleanBytes = cleanPackage.GetArray();
        byte[] originalBytes = originalPackage.GetArray();
        int unreadOffset = originalPackage.GetPos();
        int unreadLength = Math.Max(0, originalBytes.Length - unreadOffset);
        if (unreadLength == 0)
        {
            return new ZPackage(cleanBytes);
        }

        byte[] combined = new byte[cleanBytes.Length + unreadLength];
        Buffer.BlockCopy(cleanBytes, 0, combined, 0, cleanBytes.Length);
        Buffer.BlockCopy(
            originalBytes,
            unreadOffset,
            combined,
            cleanBytes.Length,
            unreadLength);
        return new ZPackage(combined);
    }
}

[HarmonyPatch(typeof(ZoneSystem), "Awake")]
internal static class ZoneSystem_Awake_LocationIconIdentity_Patch
{
    private static void Prefix()
    {
        LocationIconIdentityTransport.Clear();
    }
}

[HarmonyPatch(typeof(ZoneSystem), "SendLocationIcons")]
internal static class ZoneSystem_SendLocationIcons_Identity_Patch
{
    private static readonly MethodInfo GetLocationIcons = AccessTools.Method(
        typeof(ZoneSystem),
        nameof(ZoneSystem.GetLocationIcons),
        new[] { typeof(Dictionary<Vector3, string>) });

    private static readonly MethodInfo PopulateTransportIcons = AccessTools.Method(
        typeof(LocationIconIdentityTransport),
        nameof(LocationIconIdentityTransport.PopulateTransportIcons));

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = instructions
            .Select(static instruction => new CodeInstruction(instruction))
            .ToList();
        List<int> matches = new();
        for (int index = 0; index < codes.Count; index++)
        {
            if (codes[index].Calls(GetLocationIcons))
            {
                matches.Add(index);
            }
        }

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"YNW expected one location-icon query in ZoneSystem.SendLocationIcons; found {matches.Count}.");
        }

        CodeInstruction call = codes[matches[0]];
        call.opcode = OpCodes.Call;
        call.operand = PopulateTransportIcons;
        return codes;
    }
}

[HarmonyPatch(typeof(ZoneSystem), "RPC_LocationIcons")]
internal static class ZoneSystem_RPC_LocationIcons_Identity_Patch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(
        long sender,
        ref ZPackage pkg,
        out LocationIconIdentityTransport.ReceivedIdentitySnapshot? __state)
    {
        __state = null;
        if (!LocationIconIdentityTransport.IsTrustedSender(sender))
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                "Rejected a location-icon snapshot from a non-server routed RPC sender.");
            return false;
        }

        __state = LocationIconIdentityTransport.RewriteReceivedPackage(ref pkg);
        return true;
    }

    private static void Postfix(
        ZoneSystem __instance,
        LocationIconIdentityTransport.ReceivedIdentitySnapshot? __state)
    {
        if (__state == null)
        {
            return;
        }

        LocationIconIdentityTransport.CommitReceivedLocationNames(__instance, __state);
    }
}

[HarmonyPatch(typeof(Minimap), "UpdateLocationPins")]
internal static class Minimap_UpdateLocationPins_Patch
{
    private static readonly MethodInfo GetLocationIcons = AccessTools.Method(
        typeof(ZoneSystem),
        nameof(ZoneSystem.GetLocationIcons),
        new[] { typeof(Dictionary<Vector3, string>) });

    private static readonly MethodInfo GetVisibleLocationIcons = AccessTools.Method(
        typeof(Minimap_UpdateLocationPins_Patch),
        nameof(PopulateVisibleLocationIcons));

    [HarmonyAfter("expand_world_data")]
    [HarmonyPriority(Priority.Last)]
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = instructions
            .Select(static instruction => new CodeInstruction(instruction))
            .ToList();
        List<int> matches = new();
        for (int index = 0; index < codes.Count; index++)
        {
            if (codes[index].Calls(GetLocationIcons))
            {
                matches.Add(index);
            }
        }

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"YNW expected one location-icon query in Minimap.UpdateLocationPins; found {matches.Count}.");
        }

        CodeInstruction call = codes[matches[0]];
        call.opcode = OpCodes.Call;
        call.operand = GetVisibleLocationIcons;
        return codes;
    }

    private static void PopulateVisibleLocationIcons(
        ZoneSystem zoneSystem,
        Dictionary<Vector3, string> icons)
    {
        zoneSystem.GetLocationIcons(icons);

        List<Vector3>? hiddenPositions = null;
        foreach (KeyValuePair<Vector3, string> icon in icons)
        {
            if (!LocationIconIdentityTransport.TryGetLocationName(
                    zoneSystem,
                    icon.Key,
                    icon.Value,
                    out string prefabName)
                || !LocationIconConfigLoader.TryGetRequiredKey(prefabName, out string requiredKey)
                || PlayerKeys.HasNativeKey(Player.m_localPlayer, requiredKey))
            {
                continue;
            }

            hiddenPositions ??= new List<Vector3>();
            hiddenPositions.Add(icon.Key);
        }

        if (hiddenPositions == null)
        {
            return;
        }

        foreach (Vector3 position in hiddenPositions)
        {
            icons.Remove(position);
        }
    }
}
