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
    internal const string ModVersion = "1.0.7";
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
    private CustomSyncedValue<string>? _syncedLocationsYaml;
    private FileSystemWatcher? _yamlWatcher;
    private Coroutine? _yamlReloadCoroutine;
    private bool _progressionReloadPending;
    private bool _locationsReloadPending;
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

            if (!LocationIconConfigLoader.LoadOrCreate())
            {
                Log.LogError("No valid location-icon configuration is available. The plugin will remain disabled.");
                CleanupRuntime();
                enabled = false;
                return;
            }

            _syncedProgressionYaml = new CustomSyncedValue<string>(
                ConfigSync,
                ProgressionConfigLoader.SyncedYamlIdentifier,
                ProgressionConfigLoader.AppliedYaml);
            _syncedProgressionYaml.ValueChanged += ApplyEffectiveProgressionYaml;
            _syncedLocationsYaml = new CustomSyncedValue<string>(
                ConfigSync,
                LocationIconConfigLoader.SyncedYamlIdentifier,
                LocationIconConfigLoader.AppliedYaml);
            _syncedLocationsYaml.ValueChanged += ApplyEffectiveLocationsYaml;

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            PlayerKeyCommands.RegisterConsoleCommand();
            ItemReferenceCommands.RegisterConsoleCommand();

            SetupYamlWatcher();
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

        CustomSyncedValue<string>? syncedLocationsYaml = _syncedLocationsYaml;
        _syncedLocationsYaml = null;
        if (syncedLocationsYaml != null)
        {
            TryCleanup(
                () => syncedLocationsYaml.ValueChanged -= ApplyEffectiveLocationsYaml,
                "locations sync handler");
        }

        FileSystemWatcher? yamlWatcher = _yamlWatcher;
        _yamlWatcher = null;
        if (yamlWatcher != null)
        {
            TryCleanup(yamlWatcher.Dispose, "YAML file watcher");
        }

        Coroutine? yamlReloadCoroutine = _yamlReloadCoroutine;
        _yamlReloadCoroutine = null;
        if (yamlReloadCoroutine != null)
        {
            TryCleanup(
                () => StopCoroutine(yamlReloadCoroutine),
                "YAML reload coroutine");
        }

        _progressionReloadPending = false;
        _locationsReloadPending = false;

        TryCleanup(ItemReferenceCommands.Shutdown, "item reference command");
        TryCleanup(PlayerKeyCommands.Shutdown, "player key command");
        TryCleanup(KeyReferenceWriter.Shutdown, "key reference writer");
        TryCleanup(ItemReferenceWriter.Shutdown, "item reference writer");
        TryCleanup(LocationIconIdentityTransport.Clear, "location icon identity cache");
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

    private void SetupYamlWatcher()
    {
        Directory.CreateDirectory(ProgressionConfigLoader.ConfigDirectory);

        _yamlWatcher = new FileSystemWatcher(ProgressionConfigLoader.ConfigDirectory, "*")
        {
            IncludeSubdirectories = false,
            SynchronizingObject = ThreadingHelper.SynchronizingObject
        };
        _yamlWatcher.Changed += ReloadYamlConfiguration;
        _yamlWatcher.Created += ReloadYamlConfiguration;
        _yamlWatcher.Renamed += ReloadYamlConfiguration;
        _yamlWatcher.Deleted += ReloadYamlConfiguration;
        _yamlWatcher.Error += ReloadYamlConfigurationAfterWatcherError;
        _yamlWatcher.EnableRaisingEvents = true;
    }

    private void ReloadYamlConfiguration(object sender, FileSystemEventArgs e)
    {
        bool reloadProgression = IsTrackedYamlChange(e, ProgressionConfigLoader.ConfigFileName);
        bool reloadLocations = IsTrackedYamlChange(e, LocationIconConfigLoader.ConfigFileName);
        if (!reloadProgression && !reloadLocations)
        {
            return;
        }

        _progressionReloadPending |= reloadProgression;
        _locationsReloadPending |= reloadLocations;
        ScheduleYamlReload();
    }

    private void ReloadYamlConfigurationAfterWatcherError(object sender, ErrorEventArgs e)
    {
        Log.LogWarning($"YNW YAML file watcher error; scheduling a full configuration reload: {e.GetException()}");
        _progressionReloadPending = true;
        _locationsReloadPending = true;
        ScheduleYamlReload();
    }

    private void ScheduleYamlReload()
    {
        if (_yamlReloadCoroutine != null)
        {
            StopCoroutine(_yamlReloadCoroutine);
        }

        _yamlReloadCoroutine = StartCoroutine(ReloadYamlAfterQuietPeriod());
    }

    private IEnumerator ReloadYamlAfterQuietPeriod()
    {
        yield return new WaitForSecondsRealtime(ReloadDelaySeconds);
        bool reloadProgression = _progressionReloadPending;
        bool reloadLocations = _locationsReloadPending;
        bool progressionReloaded = !reloadProgression;
        bool locationsReloaded = !reloadLocations;

        for (int attempt = 1; attempt <= ReloadAttempts; attempt++)
        {
            if (!progressionReloaded
                && ProgressionConfigLoader.TryReadValidLocalYaml(out string progressionYaml))
            {
                ApplyReloadedProgressionYaml(progressionYaml);
                progressionReloaded = true;
                _progressionReloadPending = false;
            }

            if (!locationsReloaded
                && LocationIconConfigLoader.TryReadValidLocalYaml(out string locationsYaml))
            {
                ApplyReloadedLocationsYaml(locationsYaml);
                locationsReloaded = true;
                _locationsReloadPending = false;
            }

            if (progressionReloaded && locationsReloaded)
            {
                break;
            }

            if (attempt < ReloadAttempts)
            {
                yield return new WaitForSecondsRealtime(ReloadRetryDelaySeconds);
            }
        }

        if (!progressionReloaded)
        {
            Log.LogError("Keeping the current effective progression.yml configuration.");
            _progressionReloadPending = false;
        }

        if (!locationsReloaded)
        {
            Log.LogError("Keeping the current effective locations.yml configuration.");
            _locationsReloadPending = false;
        }

        _yamlReloadCoroutine = null;
        if (_progressionReloadPending || _locationsReloadPending)
        {
            ScheduleYamlReload();
        }
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

    private void ApplyReloadedLocationsYaml(string yaml)
    {
        if (_syncedLocationsYaml == null)
        {
            return;
        }

        if (IsRemoteClientAwaitingInitialSync())
        {
            _syncedLocationsYaml.LocalBaseValue = yaml;
            Log.LogInfo("Local locations.yml validated and queued for use outside the pending server session.");
            return;
        }

        if (!ConfigSync.IsSourceOfTruth)
        {
            _syncedLocationsYaml.AssignLocalValue(yaml);
            Log.LogInfo("Local locations.yml validated; the server-synchronized configuration remains active.");
            return;
        }

        if (!LocationIconConfigLoader.ApplyLocalYaml(yaml))
        {
            Log.LogError("Keeping the last-known-good YNW locations.yml configuration.");
            return;
        }

        _syncedLocationsYaml.AssignLocalValue(yaml);
        Log.LogInfo("locations.yml reloaded and synchronized.");
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

    private void ApplyEffectiveLocationsYaml()
    {
        try
        {
            if (_syncedLocationsYaml == null)
            {
                return;
            }

            string yaml = _syncedLocationsYaml.Value;
            if (ConfigSync.IsSourceOfTruth
                && ConfigSync.ProcessingServerUpdate
                && (UnityEngine.Object?)ZNet.instance != null
                && ZNet.instance.IsServer())
            {
                if (!string.Equals(yaml, LocationIconConfigLoader.AppliedYaml, StringComparison.Ordinal))
                {
                    Log.LogWarning("Ignored a client attempt to replace the server-authoritative locations.yml.");
                    _syncedLocationsYaml.Value = LocationIconConfigLoader.AppliedYaml;
                }

                return;
            }

            if (ConfigSync.IsSourceOfTruth
                && string.Equals(yaml, LocationIconConfigLoader.AppliedYaml, StringComparison.Ordinal))
            {
                return;
            }

            if (!LocationIconConfigLoader.ApplySyncedYaml(yaml))
            {
                Log.LogError("Rejected the synchronized locations.yml; keeping the last-known-good configuration.");
                return;
            }

            Log.LogInfo("Effective locations.yml updated from ServerSync.");
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to apply the ServerSync locations.yml: {ex}");
        }
    }

    private static bool IsTrackedYamlChange(FileSystemEventArgs e, string trackedFileName)
    {
        if (IsTrackedYamlFile(e.Name, trackedFileName))
        {
            return true;
        }

        return e is RenamedEventArgs renamed
               && IsTrackedYamlFile(renamed.OldName, trackedFileName);
    }

    private static bool IsTrackedYamlFile(string? name, string trackedFileName)
    {
        string fileName = Path.GetFileName(name ?? string.Empty);
        return string.Equals(fileName, trackedFileName, StringComparison.OrdinalIgnoreCase);
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
