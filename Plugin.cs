using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace YouAreNotWorthy;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency(ItemRestriction.InventorySlotsGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class YouAreNotWorthyPlugin : BaseUnityPlugin
{
    internal const string ModName = "YouAreNotWorthy";
    internal const string ModVersion = "1.0.1";
    internal const string Author = "sighsorry";
    internal const string ModGUID = $"{Author}.{ModName}";

    internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(ModName);

    private static readonly ConfigSync ConfigSync = new(ModGUID)
    {
        DisplayName = ModName,
        CurrentVersion = ModVersion,
        MinimumRequiredVersion = ModVersion,
        ModRequired = true,
        IsLocked = true
    };

    internal static bool IsLocalAdmin => ConfigSync.IsAdmin;
    internal static bool IsSourceOfTruth => ConfigSync.IsSourceOfTruth;
    internal static bool IsApiReady { get; private set; }

    private readonly Harmony _harmony = new(ModGUID);
    private CustomSyncedValue<string>? _syncedProgressionYaml;
    private FileSystemWatcher? _yamlWatcher;
    private Coroutine? _yamlReloadCoroutine;
    private bool _runtimeCleanedUp;

    private const float ReloadDelaySeconds = 1f;
    private const float ReloadRetryDelaySeconds = 0.5f;
    private const int ReloadAttempts = 3;

    public void Awake()
    {
        IsApiReady = false;
        try
        {
            ItemReferenceWriter.Reset();
            RequirementTranslations.Initialize();

            if (!ProgressionConfigLoader.LoadOrCreate())
            {
                Log.LogError("No valid progression configuration is available. The plugin will remain disabled.");
                CleanupRuntime();
                enabled = false;
                return;
            }

            _syncedProgressionYaml = new CustomSyncedValue<string>(
                ConfigSync,
                ProgressionConfigLoader.SyncedYamlIdentifier,
                ProgressionConfigLoader.AppliedYaml);
            _syncedProgressionYaml.ValueChanged += ApplyEffectiveProgressionYaml;

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            PlayerKeyCommands.RegisterConsoleCommand();
            ItemReferenceCommands.RegisterConsoleCommand();

            SetupProgressionWatcher();
            IsApiReady = true;
            Log.LogInfo($"{ModName} {ModVersion} loaded.");
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to initialize YNW; disabling the plugin: {ex}");
            CleanupRuntime();
            enabled = false;
        }
    }

    private void Update()
    {
        bool isSourceOfTruth = ConfigSync.IsSourceOfTruth;
        KeyReferenceWriter.TryUpdate(isSourceOfTruth);
        ItemReferenceWriter.TryUpdate(isSourceOfTruth);
        PlayerKeyCommands.Update();
    }

    private void OnDestroy()
    {
        CleanupRuntime();
    }

    private void CleanupRuntime()
    {
        if (_runtimeCleanedUp)
        {
            return;
        }

        _runtimeCleanedUp = true;
        IsApiReady = false;

        CustomSyncedValue<string>? syncedProgressionYaml = _syncedProgressionYaml;
        _syncedProgressionYaml = null;
        if (syncedProgressionYaml != null)
        {
            TryCleanup(
                () => syncedProgressionYaml.ValueChanged -= ApplyEffectiveProgressionYaml,
                "progression sync handler");
        }

        FileSystemWatcher? yamlWatcher = _yamlWatcher;
        _yamlWatcher = null;
        if (yamlWatcher != null)
        {
            TryCleanup(yamlWatcher.Dispose, "progression file watcher");
        }

        Coroutine? yamlReloadCoroutine = _yamlReloadCoroutine;
        _yamlReloadCoroutine = null;
        if (yamlReloadCoroutine != null)
        {
            TryCleanup(
                () => StopCoroutine(yamlReloadCoroutine),
                "progression reload coroutine");
        }

        TryCleanup(ItemReferenceCommands.Shutdown, "item reference command");
        TryCleanup(PlayerKeyCommands.Shutdown, "player key command");
        TryCleanup(KeyReferenceWriter.Shutdown, "key reference writer");
        TryCleanup(ItemReferenceWriter.Shutdown, "item reference writer");
        TryCleanup(_harmony.UnpatchSelf, "Harmony patches");
    }

    private static void TryCleanup(Action cleanup, string component)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to clean up YNW {component}: {ex}");
        }
    }

    private void SetupProgressionWatcher()
    {
        Directory.CreateDirectory(ProgressionConfigLoader.ConfigDirectory);

        _yamlWatcher = new FileSystemWatcher(ProgressionConfigLoader.ConfigDirectory, "*")
        {
            IncludeSubdirectories = false,
            SynchronizingObject = ThreadingHelper.SynchronizingObject
        };
        _yamlWatcher.Changed += ReloadProgressionConfig;
        _yamlWatcher.Created += ReloadProgressionConfig;
        _yamlWatcher.Renamed += ReloadProgressionConfig;
        _yamlWatcher.Deleted += ReloadProgressionConfig;
        _yamlWatcher.Error += ReloadProgressionConfigAfterWatcherError;
        _yamlWatcher.EnableRaisingEvents = true;
    }

    private void ReloadProgressionConfig(object sender, FileSystemEventArgs e)
    {
        if (!IsTrackedYamlChange(e))
        {
            return;
        }

        ScheduleProgressionReload();
    }

    private void ReloadProgressionConfigAfterWatcherError(object sender, ErrorEventArgs e)
    {
        Log.LogWarning($"YNW YAML file watcher error; scheduling a full progression.yml reload: {e.GetException()}");
        ScheduleProgressionReload();
    }

    private void ScheduleProgressionReload()
    {
        if (_yamlReloadCoroutine != null)
        {
            StopCoroutine(_yamlReloadCoroutine);
        }

        _yamlReloadCoroutine = StartCoroutine(ReloadProgressionAfterQuietPeriod());
    }

    private IEnumerator ReloadProgressionAfterQuietPeriod()
    {
        yield return new WaitForSecondsRealtime(ReloadDelaySeconds);
        for (int attempt = 1; attempt <= ReloadAttempts; attempt++)
        {
            if (ProgressionConfigLoader.TryReadValidLocalYaml(out string yaml))
            {
                ApplyReloadedProgressionYaml(yaml);
                yield break;
            }

            if (attempt < ReloadAttempts)
            {
                yield return new WaitForSecondsRealtime(ReloadRetryDelaySeconds);
            }
        }

        Log.LogError("Keeping the current effective progression.yml configuration.");
    }

    private void ApplyReloadedProgressionYaml(string yaml)
    {
        if (_syncedProgressionYaml == null)
        {
            return;
        }

        if (IsRemoteClientAwaitingInitialSync())
        {
            _syncedProgressionYaml.LocalBaseValue = yaml;
            Log.LogInfo("Local progression.yml validated and queued for use outside the pending server session.");
            return;
        }

        if (!ConfigSync.IsSourceOfTruth)
        {
            _syncedProgressionYaml.AssignLocalValue(yaml);
            Log.LogInfo("Local progression.yml validated; the server-synchronized configuration remains active.");
            return;
        }

        if (!ProgressionConfigLoader.ApplyLocalYaml(yaml))
        {
            Log.LogError("Keeping the last-known-good YNW YAML configuration.");
            return;
        }

        RefreshProgressionDependents();
        _syncedProgressionYaml.AssignLocalValue(yaml);
        Log.LogInfo("progression.yml reloaded and synchronized.");
    }

    private void ApplyEffectiveProgressionYaml()
    {
        try
        {
            if (_syncedProgressionYaml == null)
            {
                return;
            }

            string yaml = _syncedProgressionYaml.Value;
            if (ConfigSync.IsSourceOfTruth
                && ConfigSync.ProcessingServerUpdate
                && (UnityEngine.Object?)ZNet.instance != null
                && ZNet.instance.IsServer())
            {
                if (!string.Equals(yaml, ProgressionConfigLoader.AppliedYaml, StringComparison.Ordinal))
                {
                    Log.LogWarning("Ignored a client attempt to replace the server-authoritative progression.yml.");
                    _syncedProgressionYaml.Value = ProgressionConfigLoader.AppliedYaml;
                }

                return;
            }

            if (ConfigSync.IsSourceOfTruth
                && string.Equals(yaml, ProgressionConfigLoader.AppliedYaml, StringComparison.Ordinal))
            {
                return;
            }

            if (!ProgressionConfigLoader.ApplySyncedYaml(yaml))
            {
                Log.LogError("Rejected the synchronized progression.yml; keeping the last-known-good configuration.");
                return;
            }

            RefreshProgressionDependents();
            Log.LogInfo("Effective progression.yml updated from ServerSync.");
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to apply the ServerSync progression.yml: {ex}");
        }
    }

    private static bool IsTrackedYamlChange(FileSystemEventArgs e)
    {
        if (IsTrackedYamlFile(e.Name))
        {
            return true;
        }

        return e is RenamedEventArgs renamed && IsTrackedYamlFile(renamed.OldName);
    }

    private static bool IsTrackedYamlFile(string? name)
    {
        string fileName = Path.GetFileName(name ?? string.Empty);
        return string.Equals(fileName, ProgressionConfigLoader.ConfigFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRemoteClientAwaitingInitialSync()
    {
        if (!ConfigSync.IsSourceOfTruth
            || (UnityEngine.Object?)ZNet.instance == null
            || ZNet.instance.IsServer())
        {
            return false;
        }

        ZNet.ConnectionStatus status = ZNet.GetConnectionStatus();
        return status is ZNet.ConnectionStatus.Connecting or ZNet.ConnectionStatus.Connected;
    }

    private static void RefreshProgressionDependents()
    {
        try
        {
            RaidPersonalization.RefreshLocalEvents();
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to refresh local raid events after applying progression.yml: {ex}");
        }

        try
        {
            RestrictionEvaluator.RevalidateEquipment(Player.m_localPlayer);
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to revalidate equipped items after applying configuration: {ex}");
        }
    }

}
