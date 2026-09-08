using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

internal static class ItemReferenceCommands
{
    private const string CommandName = "ynw:items";
    private const string RpcRefreshRequest = "YNW_AdminItemReferenceRefresh";
    private const string RpcRefreshResult = "YNW_AdminItemReferenceResult";
    private const int MaxResultLength = 2048;
    private const int MaxPeerNameLength = 128;

    private static readonly List<string> TabOptions = new() { "refresh" };
    private static readonly FieldInfo? TerminalCommandsField = typeof(Terminal).GetField(
        "commands",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static Terminal.ConsoleCommand? _consoleCommand;
    private static bool _shutdown;

    internal static void RegisterConsoleCommand()
    {
        _shutdown = false;
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
            "Refresh the authoritative server items.reference.yml. Usage: ynw:items refresh",
            HandleConsoleCommand,
            isNetwork: true,
            optionsFetcher: static () => TabOptions);
    }

    internal static void RegisterPeer(ZNet net, ZNetPeer peer)
    {
        if (_shutdown)
        {
            return;
        }

        try
        {
            if (net.IsServer())
            {
                peer.m_rpc.Register<ZPackage>(RpcRefreshRequest, RPC_RefreshRequest);
            }
            else
            {
                peer.m_rpc.Register<string>(RpcRefreshResult, RPC_RefreshResult);
            }
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to register YNW item-reference RPCs: {ex}");
        }
    }

    internal static void Shutdown()
    {
        _shutdown = true;
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
        if (_shutdown)
        {
            return;
        }

        if (args.Length != 2
            || !string.Equals(args[1], "refresh", StringComparison.OrdinalIgnoreCase))
        {
            args.Context?.AddString("Usage: ynw:items refresh");
            return;
        }

        ZNet? net = ZNet.instance;
        if ((Object?)net == null)
        {
            args.Context?.AddString("YNW item-reference commands require an active server session.");
            return;
        }

        if (net.IsServer())
        {
            ExecuteRefresh(null, args.Context);
            return;
        }

        ZNetPeer? serverPeer = net.GetServerPeer();
        if (serverPeer == null || !serverPeer.m_rpc.IsConnected())
        {
            args.Context?.AddString("The server connection is not ready.");
            return;
        }

        serverPeer.m_rpc.Invoke(RpcRefreshRequest, new ZPackage());
        args.Context?.AddString("Requested an item-reference refresh from the server.");
    }

    private static void RPC_RefreshRequest(ZRpc requester, ZPackage _)
    {
        if (_shutdown
            || (Object?)ZNet.instance == null
            || !ZNet.instance.IsServer())
        {
            return;
        }

        if (!DirectPeerChecks.IsRemoteAdmin(requester))
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Rejected a YNW item-reference refresh from non-admin peer '{GetPeerHostName(requester)}'.");
            SendResult(requester, null, "You are not an admin on this server.");
            return;
        }

        ExecuteRefresh(requester, null);
    }

    private static void ExecuteRefresh(ZRpc? requester, Terminal? terminal)
    {
        if (_shutdown)
        {
            return;
        }

        string admin = requester == null ? "local-server" : GetPeerHostName(requester);
        YouAreNotWorthyPlugin.Log.LogInfo(
            $"Admin '{admin}' requested an authoritative item-reference refresh.");

        ItemReferenceRefreshResult result = ItemReferenceWriter.TryRefresh(
            YouAreNotWorthyPlugin.IsSourceOfTruth);
        string message = result switch
        {
            ItemReferenceRefreshResult.Updated =>
                $"Updated the server item reference from the effective runtime: {ItemReferenceWriter.ReferencePath}",
            ItemReferenceRefreshResult.Unchanged =>
                $"The server item reference already matches the effective runtime: {ItemReferenceWriter.ReferencePath}",
            ItemReferenceRefreshResult.NotAuthoritative =>
                "YNW item references can be refreshed only by the authoritative server.",
            ItemReferenceRefreshResult.RuntimeNotReady =>
                "The server runtime is not ready for an item-reference scan. Try again after the world finishes loading.",
            ItemReferenceRefreshResult.ShuttingDown =>
                "YNW is shutting down; the item reference was not refreshed.",
            _ =>
                "The server could not refresh the item reference. Check the server log; an automatic retry remains queued."
        };

        SendResult(requester, terminal, message);
    }

    private static void RPC_RefreshResult(ZRpc server, string message)
    {
        if (_shutdown)
        {
            return;
        }

        if (!DirectPeerChecks.IsServerConnection(server))
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                "Ignored a YNW item-reference result from a non-server connection.");
            return;
        }

        PrintConsole(SanitizeResult(message));
    }

    private static void SendResult(ZRpc? requester, Terminal? terminal, string message)
    {
        string safeMessage = SanitizeResult(message);
        YouAreNotWorthyPlugin.Log.LogInfo(safeMessage);
        if (requester == null)
        {
            terminal?.AddString(safeMessage);
            return;
        }

        try
        {
            requester.Invoke(RpcRefreshResult, safeMessage);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Failed to return a YNW item-reference result: {ex.Message}");
        }
    }

    private static string GetPeerHostName(ZRpc rpc)
    {
        try
        {
            string value = rpc.GetSocket().GetHostName() ?? string.Empty;
            StringBuilder builder = new(Math.Min(value.Length, MaxPeerNameLength));
            for (int index = 0; index < value.Length && index < MaxPeerNameLength; index++)
            {
                char character = value[index];
                builder.Append(char.IsControl(character) ? ' ' : character);
            }

            return builder.Length == 0 ? "unknown" : builder.ToString();
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string SanitizeResult(string? message)
    {
        string value = (message ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        return value.Length <= MaxResultLength
            ? value
            : value.Substring(0, MaxResultLength);
    }

    private static void PrintConsole(string message)
    {
        if ((Object?)global::Console.instance != null)
        {
            global::Console.instance.AddString(message);
        }
    }
}
