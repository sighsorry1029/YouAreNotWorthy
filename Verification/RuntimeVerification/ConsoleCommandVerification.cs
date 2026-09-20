using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// Uses the actual plugin entry points and original game's managed registry.
// No command action, Unity lifecycle method, or network request is executed.
internal static class ConsoleCommandVerification
{
    private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void Run(Assembly plugin, Action<string, bool> check)
    {
        FieldInfo registryField = typeof(Terminal).GetField("commands", StaticFields)
            ?? throw new MissingFieldException(typeof(Terminal).FullName, "commands");
        check("console registry uses original protected field", registryField.IsFamily);
        object savedRegistry = registryField.GetValue(null);
        var commands = new Dictionary<string, Terminal.ConsoleCommand>();
        Type keys = plugin.GetType("YouAreNotWorthy.PlayerKeyCommands", true);
        Type items = plugin.GetType("YouAreNotWorthy.ItemReferenceCommands", true);
        registryField.SetValue(null, commands);
        try
        {
            VerifyOwner(keys, "ynw:keys", new[] { "players", "list", "add", "remove" });
            VerifyOwner(items, "ynw:items", new[] { "refresh" });

            Invoke(keys, "RegisterConsoleCommand");
            Invoke(items, "RegisterConsoleCommand");
            Terminal.ConsoleCommand itemCommand = commands["ynw:items"];
            Invoke(keys, "Shutdown");
            check("console owners unregister independently",
                commands.Count == 1 && ReferenceEquals(commands["ynw:items"], itemCommand));
        }
        finally
        {
            // Restore only this verifier process's static registry, including on failure.
            Invoke(keys, "Shutdown");
            Invoke(items, "Shutdown");
            registryField.SetValue(null, savedRegistry);
        }

        void VerifyOwner(Type owner, string name, string[] options)
        {
            FieldInfo ownedField = owner.GetField("_consoleCommand", StaticFields)
                ?? throw new MissingFieldException(owner.FullName, "_consoleCommand");
            Invoke(owner, "RegisterConsoleCommand");
            Terminal.ConsoleCommand first = commands[name];
            Check("registers own instance", ReferenceEquals(first, ownedField.GetValue(null)));
            Check("preserves command flags", first.Command == name && first.IsNetwork
                && !first.IsCheat && !first.OnlyServer && !first.IsSecret && !first.AllowInDevBuild
                && !first.HideBehindDevCommands && !first.RemoteCommand && !first.OnlyAdmin);
            Check("preserves tab options", first.GetTabOptions().SequenceEqual(options));

            Invoke(owner, "RegisterConsoleCommand");
            Check("repeated registration keeps instance", ReferenceEquals(commands[name], first));
            Invoke(owner, "Shutdown");
            Check("shutdown removes owned entry", !commands.ContainsKey(name) && ownedField.GetValue(null) == null);

            Invoke(owner, "RegisterConsoleCommand");
            Terminal.ConsoleCommand foreign = ForeignCommand(name);
            Invoke(owner, "RegisterConsoleCommand");
            Check("repeated registration preserves foreign replacement", ReferenceEquals(commands[name], foreign));
            Invoke(owner, "Shutdown");
            Check("shutdown preserves foreign replacement", ReferenceEquals(commands[name], foreign) && ownedField.GetValue(null) == null);

            commands.Remove(name);
            Invoke(owner, "RegisterConsoleCommand");
            Check("can register after shutdown", !ReferenceEquals(commands[name], first) && ReferenceEquals(commands[name], ownedField.GetValue(null)));
            Invoke(owner, "Shutdown");
            Invoke(owner, "Shutdown");
            Check("repeated shutdown is harmless", commands.Count == 0);

            void Check(string label, bool passed) => check(name + ": " + label, passed);
        }
    }

    private static Terminal.ConsoleCommand ForeignCommand(string name) =>
        new Terminal.ConsoleCommand(name, "Verification-only foreign command", (Terminal.ConsoleEvent)(_ => { }));

    private static void Invoke(Type owner, string name) =>
        (owner.GetMethod(name, StaticFields) ?? throw new MissingMethodException(owner.FullName, name)).Invoke(null, null);
}
