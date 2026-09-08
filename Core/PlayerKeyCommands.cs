using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Steamworks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

internal static class PlayerKeyCommands
{
    private const string CommandName = "ynw:keys";
    private const string RpcAdminRequest = "YNW_AdminKeyRequest";
    private const string RpcAdminResult = "YNW_AdminKeyResult";
    private const string RpcTargetRequest = "YNW_AdminKeyTargetRequest";
    private const string RpcTargetResult = "YNW_AdminKeyTargetResult";
    private const float RequestTimeoutSeconds = 10f;
    private const int MaxKeyLength = 256;
    private const int MaxListedKeys = 512;
    private const int MaxListCharacters = 16 * 1024;
    private const int MaxPendingRequests = 64;
    private const int MaxPendingRequestsPerAdmin = 8;

    private static readonly List<string> TabOptions = new()
    {
        "players",
        "list",
        "add",
        "remove"
    };

    private static readonly Dictionary<long, PendingRequest> PendingRequests = new();
    private static readonly FieldInfo? TerminalCommandsField = typeof(Terminal).GetField(
        "commands",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private static Terminal.ConsoleCommand? _consoleCommand;
    private static long _nextRequestToken;

    private enum AdminOperation : byte
    {
        Players = 1,
        List = 2,
        Add = 3,
        Remove = 4
    }

    private enum TargetResult : byte
    {
        Added = 1,
        AlreadyPresent = 2,
        Removed = 3,
        NotPresent = 4,
        InvalidKey = 5,
        NoLocalPlayer = 6,
        CharacterChanged = 7,
        Listed = 8,
        Failed = 9
    }

    private sealed class OnlineTarget
    {
        internal OnlineTarget(string steamId, string playerName, ZDOID characterId, ZRpc? rpc)
        {
            SteamId = steamId;
            PlayerName = playerName;
            CharacterId = characterId;
            Rpc = rpc;
        }

        internal string SteamId { get; }
        internal string PlayerName { get; }
        internal ZDOID CharacterId { get; }
        internal ZRpc? Rpc { get; }
        internal bool IsLocal => Rpc == null;
    }

    private sealed class PendingRequest
    {
        internal PendingRequest(
            ZRpc? requester,
            ZRpc target,
            AdminOperation operation,
            string steamId,
            string playerName,
            string key,
            float deadline)
        {
            Requester = requester;
            Target = target;
            Operation = operation;
            SteamId = steamId;
            PlayerName = playerName;
            Key = key;
            Deadline = deadline;
        }

        internal ZRpc? Requester { get; }
        internal ZRpc Target { get; }
        internal AdminOperation Operation { get; }
        internal string SteamId { get; }
        internal string PlayerName { get; }
        internal string Key { get; }
        internal float Deadline { get; }
    }

    internal static void RegisterConsoleCommand()
    {
        if (_consoleCommand != null)
        {
            return;
        }

        Dictionary<string, Terminal.ConsoleCommand>? commands = GetTerminalCommands();
        if (commands == null)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Could not inspect Valheim's console-command registry; '{CommandName}' was not registered.");
            return;
        }

        if (commands.ContainsKey(CommandName))
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Could not register '{CommandName}' because another console command already uses that name.");
            return;
        }

        _consoleCommand = new Terminal.ConsoleCommand(
            CommandName,
            "Manage native personal keys for an online Steam player. Usage: ynw:keys players|list|add|remove",
            HandleConsoleCommand,
            isNetwork: true,
            optionsFetcher: static () => TabOptions);
    }

    internal static void RegisterPeer(ZNet net, ZNetPeer peer)
    {
        try
        {
            if (net.IsServer())
            {
                peer.m_rpc.Register<ZPackage>(RpcAdminRequest, RPC_AdminRequest);
                peer.m_rpc.Register<ZPackage>(RpcTargetResult, RPC_TargetResult);
            }
            else
            {
                peer.m_rpc.Register<string>(RpcAdminResult, RPC_AdminResult);
                peer.m_rpc.Register<ZPackage>(RpcTargetRequest, RPC_TargetRequest);
            }
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to register YNW admin-key RPCs: {ex}");
        }
    }

    internal static void Update()
    {
        if (PendingRequests.Count == 0)
        {
            return;
        }

        if ((Object?)ZNet.instance == null || !ZNet.instance.IsServer())
        {
            PendingRequests.Clear();
            return;
        }

        float now = Time.realtimeSinceStartup;
        List<long>? expired = null;
        foreach (KeyValuePair<long, PendingRequest> entry in PendingRequests)
        {
            if (now < entry.Value.Deadline)
            {
                continue;
            }

            expired ??= new List<long>();
            expired.Add(entry.Key);
        }

        if (expired == null)
        {
            return;
        }

        foreach (long token in expired)
        {
            if (!PendingRequests.TryGetValue(token, out PendingRequest pending))
            {
                continue;
            }

            PendingRequests.Remove(token);
            SendAdminResult(
                pending.Requester,
                $"Timed out while applying {DescribeOperation(pending.Operation)} for "
                + $"{FormatTarget(pending.PlayerName, pending.SteamId)}.");
        }
    }

    internal static void Shutdown()
    {
        PendingRequests.Clear();

        Dictionary<string, Terminal.ConsoleCommand>? commands = GetTerminalCommands();
        if (_consoleCommand != null
            && commands != null
            && commands.TryGetValue(CommandName, out Terminal.ConsoleCommand registered)
            && ReferenceEquals(registered, _consoleCommand))
        {
            commands.Remove(CommandName);
        }

        _consoleCommand = null;
    }

    private static Dictionary<string, Terminal.ConsoleCommand>? GetTerminalCommands()
    {
        try
        {
            return TerminalCommandsField?.GetValue(null)
                as Dictionary<string, Terminal.ConsoleCommand>;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void HandleConsoleCommand(Terminal.ConsoleEventArgs args)
    {
        if ((Object?)ZNet.instance == null)
        {
            args.Context?.AddString("YNW player-key commands require an active server session.");
            return;
        }

        if (ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
        {
            args.Context?.AddString("YNW player-key commands support Steam networking only.");
            return;
        }

        if (!TryParseCommand(args, out AdminOperation operation, out string steamId, out string key))
        {
            PrintUsage(args.Context);
            return;
        }

        ZPackage request = new();
        request.Write((byte)operation);
        request.Write(steamId);
        request.Write(key);

        if (ZNet.instance.IsServer())
        {
            request.SetPos(0);
            HandleAdminRequest(null, request);
            return;
        }

        ZNetPeer? serverPeer = ZNet.instance.GetServerPeer();
        if (serverPeer == null || !serverPeer.m_rpc.IsConnected())
        {
            args.Context?.AddString("The server connection is not ready.");
            return;
        }

        serverPeer.m_rpc.Invoke(RpcAdminRequest, request);
    }

    private static bool TryParseCommand(
        Terminal.ConsoleEventArgs args,
        out AdminOperation operation,
        out string steamId,
        out string key)
    {
        operation = default;
        steamId = string.Empty;
        key = string.Empty;

        string action = args.Length >= 2 ? (args[1] ?? string.Empty).Trim() : string.Empty;
        if (string.Equals(action, "players", StringComparison.OrdinalIgnoreCase))
        {
            operation = AdminOperation.Players;
            return args.Length == 2;
        }

        if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
        {
            operation = AdminOperation.List;
            return args.Length == 3 && TryNormalizeSteamId(args[2], out steamId);
        }

        if (string.Equals(action, "add", StringComparison.OrdinalIgnoreCase))
        {
            operation = AdminOperation.Add;
        }
        else if (string.Equals(action, "remove", StringComparison.OrdinalIgnoreCase))
        {
            operation = AdminOperation.Remove;
        }
        else
        {
            return false;
        }

        return args.Length == 4
               && TryNormalizeSteamId(args[2], out steamId)
               && TryNormalizeKey(args[3], out key);
    }

    private static void PrintUsage(Terminal? terminal)
    {
        terminal?.AddString("YNW online personal-key commands:");
        terminal?.AddString("  ynw:keys players");
        terminal?.AddString("  ynw:keys list <steamid64>");
        terminal?.AddString("  ynw:keys add <steamid64> <key>");
        terminal?.AddString("  ynw:keys remove <steamid64> <key>");
    }

    private static void RPC_AdminRequest(ZRpc requester, ZPackage request)
    {
        if ((Object?)ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        if (!DirectPeerChecks.IsRemoteAdmin(requester))
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Rejected YNW player-key command from non-admin peer '{GetPeerHostName(requester)}'.");
            SendAdminResult(requester, "You are not an admin on this server.");
            return;
        }

        HandleAdminRequest(requester, request);
    }

    private static void HandleAdminRequest(ZRpc? requester, ZPackage request)
    {
        try
        {
            if (ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
            {
                SendAdminResult(requester, "YNW player-key commands support Steam networking only.");
                return;
            }

            AdminOperation operation = (AdminOperation)request.ReadByte();
            string requestedSteamId = request.ReadString();
            string requestedKey = request.ReadString();

            if (operation == AdminOperation.Players)
            {
                SendAdminResult(requester, BuildOnlinePlayersMessage());
                return;
            }

            if (operation is not (AdminOperation.List or AdminOperation.Add or AdminOperation.Remove)
                || !TryNormalizeSteamId(requestedSteamId, out string steamId))
            {
                SendAdminResult(requester, "Invalid YNW player-key command request.");
                return;
            }

            string key = string.Empty;
            if (operation is AdminOperation.Add or AdminOperation.Remove
                && !TryNormalizeKey(requestedKey, out key))
            {
                SendAdminResult(requester, "The personal key is invalid or is shared world state.");
                return;
            }

            List<OnlineTarget> matches = GetOnlineTargets().FindAll(
                target => string.Equals(target.SteamId, steamId, StringComparison.Ordinal));
            if (matches.Count == 0)
            {
                SendAdminResult(requester, $"Steam player {steamId} is not online with a loaded character.");
                return;
            }

            if (matches.Count > 1)
            {
                SendAdminResult(requester, $"Steam player {steamId} resolved to more than one connection; no key was changed.");
                return;
            }

            OnlineTarget target = matches[0];
            LogAdminRequest(requester, operation, target, key);
            if (target.IsLocal)
            {
                SendLocalTargetResult(requester, operation, target, key);
                return;
            }

            SendTargetRequest(requester, operation, target, key);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to handle a YNW player-key admin request: {ex}");
            SendAdminResult(requester, "The YNW player-key request failed on the server. Check the server log.");
        }
    }

    private static void SendTargetRequest(
        ZRpc? requester,
        AdminOperation operation,
        OnlineTarget target,
        string key)
    {
        if (PendingRequests.Count >= MaxPendingRequests
            || CountPendingRequests(requester) >= MaxPendingRequestsPerAdmin)
        {
            SendAdminResult(requester, "Too many YNW player-key requests are awaiting target responses. Try again shortly.");
            return;
        }

        ZRpc targetRpc = target.Rpc!;
        if (!targetRpc.IsConnected())
        {
            SendAdminResult(requester, $"{FormatTarget(target.PlayerName, target.SteamId)} disconnected before the request was sent.");
            return;
        }

        long token = NextRequestToken();
        PendingRequests[token] = new PendingRequest(
            requester,
            targetRpc,
            operation,
            target.SteamId,
            target.PlayerName,
            key,
            Time.realtimeSinceStartup + RequestTimeoutSeconds);

        ZPackage request = new();
        request.Write(token);
        request.Write((byte)operation);
        request.Write(target.CharacterId);
        request.Write(key);

        try
        {
            targetRpc.Invoke(RpcTargetRequest, request);
        }
        catch (Exception ex)
        {
            PendingRequests.Remove(token);
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Failed to send YNW player-key request to {FormatTarget(target.PlayerName, target.SteamId)}: {ex}");
            SendAdminResult(requester, $"Could not contact {FormatTarget(target.PlayerName, target.SteamId)}.");
        }
    }

    private static void RPC_TargetRequest(ZRpc server, ZPackage request)
    {
        if (!DirectPeerChecks.IsServerConnection(server))
        {
            YouAreNotWorthyPlugin.Log.LogWarning("Ignored a YNW player-key target request from a non-server connection.");
            return;
        }

        long token = 0;
        ZPackage response;
        try
        {
            token = request.ReadLong();
            AdminOperation operation = (AdminOperation)request.ReadByte();
            ZDOID expectedCharacter = request.ReadZDOID();
            string requestedKey = request.ReadString();
            response = BuildTargetResponse(token, operation, expectedCharacter, requestedKey);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to apply a YNW player-key target request: {ex}");
            response = new ZPackage();
            response.Write(token);
            response.Write((byte)TargetResult.Failed);
        }

        server.Invoke(RpcTargetResult, response);
    }

    private static ZPackage BuildTargetResponse(
        long token,
        AdminOperation operation,
        ZDOID expectedCharacter,
        string requestedKey)
    {
        ZPackage response = new();
        response.Write(token);
        Player? player = Player.m_localPlayer;
        if ((Object?)player == null)
        {
            response.Write((byte)TargetResult.NoLocalPlayer);
        }
        else if (player.GetZDOID() != expectedCharacter)
        {
            response.Write((byte)TargetResult.CharacterChanged);
        }
        else if (operation == AdminOperation.List)
        {
            WriteKeyListResponse(response, player);
        }
        else if (operation is AdminOperation.Add or AdminOperation.Remove)
        {
            WriteMutationResponse(response, requestedKey, operation == AdminOperation.Add);
        }
        else
        {
            response.Write((byte)TargetResult.Failed);
        }

        return response;
    }

    private static void WriteMutationResponse(ZPackage response, string requestedKey, bool add)
    {
        if (!TryNormalizeKey(requestedKey, out string key))
        {
            response.Write((byte)TargetResult.InvalidKey);
            return;
        }

        PersonalKeyMutationResult result = PlayerKeys.MutateLocal(key, add);
        TargetResult targetResult = result switch
        {
            PersonalKeyMutationResult.Added => TargetResult.Added,
            PersonalKeyMutationResult.AlreadyPresent => TargetResult.AlreadyPresent,
            PersonalKeyMutationResult.Removed => TargetResult.Removed,
            PersonalKeyMutationResult.NotPresent => TargetResult.NotPresent,
            PersonalKeyMutationResult.InvalidKey => TargetResult.InvalidKey,
            PersonalKeyMutationResult.NoLocalPlayer => TargetResult.NoLocalPlayer,
            _ => TargetResult.Failed
        };

        response.Write((byte)targetResult);
        if (targetResult is TargetResult.Added or TargetResult.Removed)
        {
            response.Write(TrySaveLocalProfile());
        }
    }

    private static void WriteKeyListResponse(ZPackage response, Player player)
    {
        List<string> keys = new(player.GetUniqueKeys());
        keys.RemoveAll(static key => string.IsNullOrWhiteSpace(key));
        keys.Sort(StringComparer.OrdinalIgnoreCase);

        int included = Math.Min(keys.Count, MaxListedKeys);
        response.Write((byte)TargetResult.Listed);
        response.Write(keys.Count);
        response.Write(included);
        for (int i = 0; i < included; i++)
        {
            response.Write(SanitizeForConsole(keys[i], MaxKeyLength));
        }
    }

    private static void RPC_TargetResult(ZRpc target, ZPackage response)
    {
        if ((Object?)ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        long token;
        try
        {
            token = response.ReadLong();
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogWarning($"Ignored a malformed YNW player-key result: {ex.Message}");
            return;
        }

        if (!PendingRequests.TryGetValue(token, out PendingRequest pending))
        {
            YouAreNotWorthyPlugin.Log.LogWarning("Ignored an unknown or expired YNW player-key result.");
            return;
        }

        if (!ReferenceEquals(pending.Target, target))
        {
            YouAreNotWorthyPlugin.Log.LogWarning("Ignored a YNW player-key result from the wrong peer.");
            return;
        }

        PendingRequests.Remove(token);
        try
        {
            string message = ReadTargetResultMessage(
                pending.Operation,
                pending.SteamId,
                pending.PlayerName,
                pending.Key,
                response);
            SendAdminResult(pending.Requester, message);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogWarning($"Failed to read a YNW player-key result: {ex}");
            SendAdminResult(pending.Requester, "The target returned an invalid YNW player-key result.");
        }
    }

    private static string ReadTargetResultMessage(
        AdminOperation operation,
        string steamId,
        string playerName,
        string key,
        ZPackage response)
    {
        TargetResult result = (TargetResult)response.ReadByte();
        if (!IsCompatibleTargetResult(operation, result))
        {
            throw new InvalidOperationException(
                $"Target result '{result}' is not valid for operation '{operation}'.");
        }

        return BuildTargetResultMessage(steamId, playerName, key, result, response);
    }

    private static string BuildTargetResultMessage(
        string steamId,
        string playerName,
        string key,
        TargetResult result,
        ZPackage response)
    {
        string target = FormatTarget(playerName, steamId);
        switch (result)
        {
            case TargetResult.Added:
                return AppendSaveWarning($"Added personal key '{key}' to {target}.", response.ReadBool());
            case TargetResult.AlreadyPresent:
                return $"{target} already has personal key '{key}'.";
            case TargetResult.Removed:
                return AppendSaveWarning($"Removed personal key '{key}' from {target}.", response.ReadBool());
            case TargetResult.NotPresent:
                return $"{target} does not have personal key '{key}'.";
            case TargetResult.InvalidKey:
                return $"Personal key '{key}' was rejected by {target}.";
            case TargetResult.NoLocalPlayer:
                return $"{target} no longer has a loaded local character.";
            case TargetResult.CharacterChanged:
                return $"{target} changed character before the key operation completed.";
            case TargetResult.Listed:
                return ReadKeyListMessage(playerName, steamId, response);
            default:
                return $"The personal-key operation failed for {target}.";
        }
    }

    private static bool IsCompatibleTargetResult(AdminOperation operation, TargetResult result)
    {
        if (result is TargetResult.NoLocalPlayer or TargetResult.CharacterChanged or TargetResult.Failed)
        {
            return true;
        }

        return operation switch
        {
            AdminOperation.List => result == TargetResult.Listed,
            AdminOperation.Add => result is TargetResult.Added
                or TargetResult.AlreadyPresent
                or TargetResult.InvalidKey,
            AdminOperation.Remove => result is TargetResult.Removed
                or TargetResult.NotPresent
                or TargetResult.InvalidKey,
            _ => false
        };
    }

    private static string ReadKeyListMessage(string playerName, string steamId, ZPackage response)
    {
        int total = response.ReadInt();
        int included = response.ReadInt();
        if (total < 0 || included < 0 || included > total || included > MaxListedKeys)
        {
            throw new InvalidOperationException("The target returned an invalid personal-key count.");
        }

        StringBuilder message = new();
        message.Append("Personal keys for ").Append(FormatTarget(playerName, steamId));
        message.Append(" (").Append(total.ToString(CultureInfo.InvariantCulture)).AppendLine("):");

        int written = 0;
        for (int i = 0; i < included; i++)
        {
            string key = SanitizeForConsole(response.ReadString(), MaxKeyLength);
            if (message.Length + key.Length + 4 > MaxListCharacters)
            {
                continue;
            }

            message.Append("  ").AppendLine(key);
            written++;
        }

        if (written < total)
        {
            message.Append("  ... ")
                .Append((total - written).ToString(CultureInfo.InvariantCulture))
                .Append(" more key(s) omitted");
        }

        return message.ToString().TrimEnd();
    }

    private static void SendLocalTargetResult(
        ZRpc? requester,
        AdminOperation operation,
        OnlineTarget target,
        string key)
    {
        ZPackage response = BuildTargetResponse(0, operation, target.CharacterId, key);
        response.SetPos(0);
        try
        {
            _ = response.ReadLong();
            string message = ReadTargetResultMessage(
                operation,
                target.SteamId,
                target.PlayerName,
                key,
                response);
            SendAdminResult(requester, message);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogWarning($"Failed to read a YNW player-key result: {ex}");
            SendAdminResult(requester, "The target returned an invalid YNW player-key result.");
        }
    }

    private static string BuildOnlinePlayersMessage()
    {
        List<OnlineTarget> targets = GetOnlineTargets();
        targets.Sort(static (left, right) => string.Compare(left.SteamId, right.SteamId, StringComparison.Ordinal));

        if (targets.Count == 0)
        {
            return "No Steam players with loaded characters are online.";
        }

        StringBuilder message = new("Online YNW player-key targets:");
        foreach (OnlineTarget target in targets)
        {
            message.AppendLine()
                .Append("  ")
                .Append(SanitizeForConsole(target.PlayerName, 64))
                .Append(" | ")
                .Append(target.SteamId);
        }

        return message.ToString();
    }

    private static List<OnlineTarget> GetOnlineTargets()
    {
        List<OnlineTarget> targets = new();
        ZNet? net = ZNet.instance;
        if ((Object?)net == null || !net.IsServer())
        {
            return targets;
        }

        Player? localPlayer = Player.m_localPlayer;
        if ((Object?)localPlayer != null
            && TryGetLocalSteamId(out string localSteamId)
            && !localPlayer.GetZDOID().IsNone())
        {
            targets.Add(new OnlineTarget(
                localSteamId,
                localPlayer.GetPlayerName(),
                localPlayer.GetZDOID(),
                null));
        }

        foreach (ZNetPeer peer in net.GetConnectedPeers())
        {
            if (!peer.IsReady()
                || !peer.m_rpc.IsConnected()
                || peer.m_characterID.IsNone()
                || !TryNormalizeConnectedSteamId(peer.m_socket.GetHostName(), out string steamId))
            {
                continue;
            }

            targets.Add(new OnlineTarget(
                steamId,
                peer.m_playerName,
                peer.m_characterID,
                peer.m_rpc));
        }

        return targets;
    }

    private static bool TryGetLocalSteamId(out string steamId)
    {
        steamId = string.Empty;
        try
        {
            return SteamUser.BLoggedOn()
                   && TryNormalizeSteamId(SteamUser.GetSteamID().ToString(), out steamId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryNormalizeConnectedSteamId(string? value, out string steamId)
    {
        string candidate = (value ?? string.Empty).Trim();
        if (candidate.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate.Substring("Steam_".Length);
        }

        return TryNormalizeSteamId(candidate, out steamId);
    }

    private static bool TryNormalizeSteamId(string? value, out string steamId)
    {
        string candidate = (value ?? string.Empty).Trim();
        if (candidate.Length == 17
            && ulong.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed)
            && parsed != 0
            && new CSteamID(parsed).IsValid())
        {
            steamId = parsed.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        steamId = string.Empty;
        return false;
    }

    private static bool TryNormalizeKey(string? value, out string key)
    {
        string candidate = (value ?? string.Empty).Trim();
        if (candidate.Length == 0 || candidate.Length > MaxKeyLength)
        {
            key = string.Empty;
            return false;
        }

        foreach (char character in candidate)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                key = string.Empty;
                return false;
            }
        }

        return ProgressionIndex.TryResolvePersonalKey(candidate, out key);
    }

    private static bool TrySaveLocalProfile()
    {
        try
        {
            if ((Object?)Game.instance == null)
            {
                return false;
            }

            Game.instance.SavePlayerProfile(setLogoutPoint: false);
            return true;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to save the local character after an admin key change: {ex}");
            return false;
        }
    }

    private static void RPC_AdminResult(ZRpc server, string message)
    {
        if (!DirectPeerChecks.IsServerConnection(server))
        {
            YouAreNotWorthyPlugin.Log.LogWarning("Ignored a YNW admin-key result from a non-server connection.");
            return;
        }

        PrintConsole(message);
    }

    private static void SendAdminResult(ZRpc? requester, string message)
    {
        string safeMessage = SanitizeMultilineForConsole(message, MaxListCharacters);
        YouAreNotWorthyPlugin.Log.LogInfo(safeMessage);
        if (requester == null)
        {
            PrintConsole(safeMessage);
            return;
        }

        try
        {
            requester.Invoke(RpcAdminResult, safeMessage);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogWarning($"Failed to return a YNW admin-key result: {ex.Message}");
        }
    }

    private static void PrintConsole(string message)
    {
        if ((Object?)global::Console.instance != null)
        {
            global::Console.instance.AddString(message);
        }
    }

    private static void LogAdminRequest(
        ZRpc? requester,
        AdminOperation operation,
        OnlineTarget target,
        string key)
    {
        string admin = requester == null ? "local-server" : GetPeerHostName(requester);
        string keySuffix = key.Length == 0 ? string.Empty : $", key='{key}'";
        YouAreNotWorthyPlugin.Log.LogInfo(
            $"Admin '{admin}' requested {DescribeOperation(operation)} for "
            + $"{FormatTarget(target.PlayerName, target.SteamId)}{keySuffix}.");
    }

    private static string GetPeerHostName(ZRpc rpc)
    {
        try
        {
            return SanitizeForConsole(rpc.GetSocket().GetHostName(), 128);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string FormatTarget(string playerName, string steamId)
    {
        return $"{SanitizeForConsole(playerName, 64)} ({steamId})";
    }

    private static string DescribeOperation(AdminOperation operation)
    {
        return operation switch
        {
            AdminOperation.List => "personal-key listing",
            AdminOperation.Add => "personal-key addition",
            AdminOperation.Remove => "personal-key removal",
            _ => "personal-key operation"
        };
    }

    private static string AppendSaveWarning(string message, bool saved)
    {
        return saved ? message : message + " The in-memory change succeeded, but saving the character failed.";
    }

    private static long NextRequestToken()
    {
        do
        {
            _nextRequestToken++;
            if (_nextRequestToken <= 0)
            {
                _nextRequestToken = 1;
            }
        }
        while (PendingRequests.ContainsKey(_nextRequestToken));

        return _nextRequestToken;
    }

    private static int CountPendingRequests(ZRpc? requester)
    {
        int count = 0;
        foreach (PendingRequest pending in PendingRequests.Values)
        {
            if (ReferenceEquals(pending.Requester, requester))
            {
                count++;
            }
        }

        return count;
    }

    private static string SanitizeForConsole(string? value, int maxLength)
    {
        string raw = value ?? string.Empty;
        StringBuilder sanitized = new(Math.Min(raw.Length, maxLength));
        foreach (char character in raw)
        {
            if (sanitized.Length >= maxLength)
            {
                break;
            }

            sanitized.Append(char.IsControl(character) ? '?' : character);
        }

        return sanitized.ToString();
    }

    private static string SanitizeMultilineForConsole(string? value, int maxLength)
    {
        string raw = value ?? string.Empty;
        StringBuilder sanitized = new(Math.Min(raw.Length, maxLength));
        foreach (char character in raw)
        {
            if (sanitized.Length >= maxLength)
            {
                break;
            }

            if (character == '\n')
            {
                sanitized.Append(character);
            }
            else if (character != '\r')
            {
                sanitized.Append(char.IsControl(character) ? '?' : character);
            }
        }

        return sanitized.ToString();
    }
}
