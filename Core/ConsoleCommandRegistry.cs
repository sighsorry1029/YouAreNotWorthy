using System;
using System.Collections.Generic;
using System.Reflection;

namespace YouAreNotWorthy;

// Terminal's constructor registers immediately. Check names before constructing
// commands, and remove only the instance registered by the requesting owner.
internal static class ConsoleCommandRegistry
{
    private static readonly FieldInfo? CommandsField = typeof(Terminal).GetField(
        "commands",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    internal static bool CanRegister(string name)
    {
        Dictionary<string, Terminal.ConsoleCommand>? commands = GetCommands();
        if (commands == null)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Could not inspect Valheim's console-command registry; '{name}' was not registered.");
            return false;
        }

        if (commands.ContainsKey(name))
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Could not register '{name}' because another console command already uses that name.");
            return false;
        }

        return true;
    }

    internal static void Unregister(string name, Terminal.ConsoleCommand? command)
    {
        Dictionary<string, Terminal.ConsoleCommand>? commands = GetCommands();
        if (command != null
            && commands != null
            && commands.TryGetValue(name, out Terminal.ConsoleCommand registered)
            && ReferenceEquals(registered, command))
        {
            commands.Remove(name);
        }
    }

    private static Dictionary<string, Terminal.ConsoleCommand>? GetCommands()
    {
        try
        {
            return CommandsField?.GetValue(null)
                as Dictionary<string, Terminal.ConsoleCommand>;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
