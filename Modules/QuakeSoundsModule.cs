using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuakeSounds.Managers;
using QuakeSounds.Models;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEvents;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace QuakeSounds.Modules;

internal sealed class QuakeSoundsModule : IModule, IGameListener, IClientListener, IEventListener
{
    private readonly InterfaceBridge            _bridge;
    private readonly ILogger<QuakeSoundsModule> _logger;
    private readonly SoundPackManager           _soundPackManager;

    // Per-player tracking (slot-indexed)
    private readonly int[]    _killStreaks    = new int[64]; // kills without dying — PERSISTS across rounds
    private readonly double[] _lastKillTime   = new double[64]; // engine time of last kill (combo window)
    private readonly int[]    _comboCount     = new int[64]; // rapid multikill — expires on the rapid window
    private readonly int[]    _headshotStreak = new int[64]; // consecutive headshot kills in a life
    private bool _firstBloodOccurred;

    // Sound coalescing: many categories (combo + killstreak + headshot) and near-simultaneous
    // kills can all want to play in the same instant → overlapping cacophony. Buffer candidates
    // for a short window and play only the single highest-priority winner.
    private int          _pendingPriority = int.MinValue;
    private string?      _pendingSoundKey;
    private IGameClient? _pendingClient;   // null = announce to everyone; else personal listener
    private IGameClient? _pendingInvoker;  // whose soundpack to resolve from (e.g. the killer); null = default pack
    private bool         _pendingScheduled;

    // Per-player volume (slot-indexed), null = use server default
    private readonly float?[] _volumeCache = new float?[64];
    private const string VolumeCookieKey = "quakesounds_volume";

    // ConVars
    private IConVar? _cvEnabled;
    private IConVar? _cvEnableKillStreaks;
    private IConVar? _cvEnableFirstBlood;
    private IConVar? _cvEnableHeadshot;
    private IConVar? _cvEnableCenterMsg;
    private IConVar? _cvEnableDuringWarmup;
    private IConVar? _cvResetOnDeath;
    private IConVar? _cvRapidKillWindow;
    private IConVar? _cvVolume;
    private IConVar? _cvSoundDebounce;

    // Cached module interfaces
    private IClientPreference? _clientPreference;
    private ILocalizerManager? _localizerManager;
    private IMenuManager?      _menuManager;

    private const string MuteCookieKey = "quakesounds_muted";
    private readonly bool?[] _muteCache = new bool?[64];

    // Volume presets
    private static readonly (float value, string label)[] VolumePresets =
    [
        (1.00f, "100%"),
        (0.75f, "75%"),
        (0.50f, "50%"),
        (0.25f, "25%"),
        (0.10f, "10%"),
    ];

    // Killstreak ladder (kills without dying, persists across rounds). Fires when the streak
    // EQUALS a tier. Sound pack key == soundevent local name (quake.<key>).
    private static readonly Dictionary<int, string> KillstreakTiers = new()
    {
        [4]  = "dominating",   [6]  = "rampage",       [8]  = "killingspree",
        [10] = "monsterkill",  [14] = "unstoppable",   [16] = "ultrakill",
        [18] = "godlike",      [20] = "wickedsick",    [22] = "impressive",
        [24] = "ludicrouskill",[26] = "holyshit",      [30] = "massacre",
        [35] = "maniac",       [40] = "killingmachine",[45] = "ownage",
        [50] = "unreal",       [60] = "flawlessvictory",
    };

    // Combo ladder (rapid multikill, expires on the rapid window). 7+ = comboking (cap).
    private static readonly Dictionary<int, string> ComboTiers = new()
    {
        [2] = "doublekill", [3] = "triplekill", [4] = "multikill",
        [5] = "megakill",   [6] = "hexakill",   [7] = "comboking",
    };

    // Headshot ladder (consecutive headshot kills in a life). Milestones; otherwise base headshot.
    private static readonly Dictionary<int, string> HeadshotTiers = new()
    {
        [3] = "headhunter", [5] = "eagleeye", [7] = "bullseye", [9] = "assassin", [11] = "outstanding",
    };

    public QuakeSoundsModule(InterfaceBridge bridge, ILogger<QuakeSoundsModule> logger, SoundPackManager soundPackManager)
    {
        _bridge = bridge;
        _logger = logger;
        _soundPackManager = soundPackManager;
    }

    public bool Init()
    {
        _cvEnabled           = _bridge.ConVarManager.CreateConVar("qs_enabled",           true,  "Enable QuakeSounds plugin");
        _cvEnableKillStreaks  = _bridge.ConVarManager.CreateConVar("qs_killstreaks",       true,  "Enable kill streak sounds");
        _cvEnableFirstBlood   = _bridge.ConVarManager.CreateConVar("qs_firstblood",        true,  "Enable first blood sound");
        _cvEnableHeadshot     = _bridge.ConVarManager.CreateConVar("qs_headshot",          true,  "Enable headshot kill sound");
        _cvEnableCenterMsg    = _bridge.ConVarManager.CreateConVar("qs_center_message",    true,  "Enable center HTML messages");
        _cvEnableDuringWarmup = _bridge.ConVarManager.CreateConVar("qs_during_warmup",     false, "Enable sounds during warmup");
        _cvResetOnDeath       = _bridge.ConVarManager.CreateConVar("qs_reset_on_death",    true,  "Reset streak on death");
        _cvRapidKillWindow    = _bridge.ConVarManager.CreateConVar("qs_rapid_kill_window", 4.0f,  "Time window (seconds) for rapid multi-kills");
        _cvVolume             = _bridge.ConVarManager.CreateConVar("qs_volume",            1.0f,  "Sound volume (0.0-1.0) — server default");
        _cvSoundDebounce      = _bridge.ConVarManager.CreateConVar("qs_sound_debounce",    0.1f,  "Window (seconds) to coalesce overlapping sounds; only the highest-priority one plays");

        _bridge.ModSharp.InstallGameListener(this);
        _bridge.ClientManager.InstallClientListener(this);

        // Hook player_death (richer than the killed forward: gives Weapon, Headshot, Revenge).
        _bridge.EventManager.InstallEventListener(this);
        _bridge.EventManager.HookEvent("player_death");

        _bridge.ClientManager.InstallCommandCallback("quakemute", OnQuakeMuteCommand);
        _bridge.ClientManager.InstallCommandCallback("quakepack", OnQuakePackCommand);
        _bridge.ClientManager.InstallCommandCallback("quake", OnQuakeMenuCommand);
        _bridge.ClientManager.InstallCommandCallback("qs", OnQuakeMenuCommand);

        _logger.LogInformation("[QuakeSounds] Initialized");
        return true;
    }

    public void OnAllModulesLoaded(ServiceProvider provider)
    {
        _clientPreference = _bridge.GetClientPreference();
        _localizerManager = _bridge.GetLocalizerManager();
        _menuManager      = _bridge.GetMenuManager();

        if (_clientPreference is null)
            _logger.LogWarning("[QuakeSounds] ClientPreference not available — mute/pack/volume won't persist");

        if (_menuManager is null)
            _logger.LogWarning("[QuakeSounds] MenuManager not available — .quake/.qs menu won't work");
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);
        _bridge.ClientManager.RemoveClientListener(this);
        _bridge.EventManager.RemoveEventListener(this);

        _bridge.ClientManager.RemoveCommandCallback("quakemute", OnQuakeMuteCommand);
        _bridge.ClientManager.RemoveCommandCallback("quakepack", OnQuakePackCommand);
        _bridge.ClientManager.RemoveCommandCallback("quake", OnQuakeMenuCommand);
        _bridge.ClientManager.RemoveCommandCallback("qs", OnQuakeMenuCommand);

        Array.Clear(_killStreaks);
        Array.Clear(_lastKillTime);
        Array.Clear(_comboCount);
        Array.Clear(_headshotStreak);
        Array.Clear(_muteCache);
        Array.Clear(_volumeCache);
    }

    #region IGameListener

    public int ListenerPriority => 0;
    public int ListenerVersion  => IGameListener.ApiVersion;

    void IGameListener.OnResourcePrecache()
    {
        // Register the soundevent (.vsndevts) files so quake.* events are playable. Without this
        // EmitSoundClient resolves nothing and is silent. Guard each so one bad/missing entry
        // can't abort precache. Missing/unmounted files no-op silently engine-side.
        foreach (var file in _soundPackManager.PrecacheFiles)
        {
            try
            {
                _bridge.ModSharp.PrecacheResource(file);
                _logger.LogInformation("[QuakeSounds] Precached soundevent file {File}", file);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[QuakeSounds] Failed to precache {File}", file);
            }
        }
    }

    public void OnRoundRestarted()
    {
        // Killstreak + headshot streak PERSIST across rounds (reset only on death).
        // Combo is short-fuse, and first blood resets for the new round.
        Array.Clear(_lastKillTime);
        Array.Clear(_comboCount);
        _firstBloodOccurred = false;

        // Round-start announce ("prepare").
        if (_cvEnabled is { } cv && !cv.GetBool())
            return;
        if (_cvEnableDuringWarmup is { } wc && !wc.GetBool() && _bridge.GameRules.IsWarmupPeriod)
            return;
        PlaySoundToAll("prepare", null);
    }

    #endregion

    #region IClientListener

    int IClientListener.ListenerPriority => 0;
    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;

    public void OnClientPutInServer(IGameClient client)
    {
        if (client.IsFakeClient) return;

        _killStreaks[client.Slot]    = 0;
        _lastKillTime[client.Slot]   = 0;
        _comboCount[client.Slot]     = 0;
        _headshotStreak[client.Slot] = 0;
        LoadMutePreference(client);
        LoadVolumePreference(client);
        _soundPackManager.LoadPlayerPack(client);
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        _killStreaks[client.Slot]    = 0;
        _lastKillTime[client.Slot]   = 0;
        _comboCount[client.Slot]     = 0;
        _headshotStreak[client.Slot] = 0;
        _muteCache[client.Slot]      = null;
        _volumeCache[client.Slot]    = null;
        _soundPackManager.ClearPlayerCache(client.Slot);
    }

    #endregion

    #region Kill Handling

    // ===== IEventListener =====

    int IEventListener.ListenerPriority => 0;
    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;

    public void FireGameEvent(IGameEvent @event)
    {
        if (@event is IEventPlayerDeath death)
            HandlePlayerDeath(death);
    }

    private void HandlePlayerDeath(IEventPlayerDeath death)
    {
        if (_cvEnabled is { } cv && !cv.GetBool())
            return;
        if (_cvEnableDuringWarmup is { } wc && !wc.GetBool() && _bridge.GameRules.IsWarmupPeriod)
            return;

        var victim   = death.VictimController;
        var attacker = death.KillerController;
        if (victim is null)
            return;

        var victimSlot = (int) victim.PlayerSlot.AsPrimitive();

        // Death resets the victim's counters. Killstreak (dominating…flawlessvictory) PERSISTS
        // across rounds and only dies here — gated on qs_reset_on_death so a server can opt to
        // keep streaks through death too. Combo/headshot streaks always reset (short-fuse).
        if (_cvResetOnDeath is not { } rod || rod.GetBool())
            _killStreaks[victimSlot] = 0;
        _comboCount[victimSlot]     = 0;
        _headshotStreak[victimSlot] = 0;

        // Suicide / world death (no attacker or self). Pack comes from the victim
        // (they're the only player involved). World deaths fall back to default pack.
        if (attacker is null || attacker.PlayerSlot == victim.PlayerSlot)
        {
            QueueSound("pancake", PrioPancake, invoker: victim.GetGameClient());
            return;
        }

        var killerClient = attacker.GetGameClient();
        if (killerClient is not { IsInGame: true })
            return;

        // Team kill — announce with killer's pack, don't reward a streak.
        if (attacker.Team == victim.Team)
        {
            QueueSound("teamkiller", PrioTeamkill, invoker: killerClient);
            return;
        }

        var slot = (int) attacker.PlayerSlot.AsPrimitive();
        var now  = _bridge.ModSharp.EngineTime();

        // Persistent killstreak + short-fuse combo.
        _killStreaks[slot]++;
        var rapidWindow = _cvRapidKillWindow?.GetFloat() ?? 4f;
        _comboCount[slot] = (now - _lastKillTime[slot] <= rapidWindow) ? _comboCount[slot] + 1 : 1;
        _lastKillTime[slot] = now;

        // Consecutive-headshot streak (broken by a non-headshot kill).
        _headshotStreak[slot] = death.Headshot ? _headshotStreak[slot] + 1 : 0;

        // ── Candidates compete by priority; the 0.1s window flushes only the winner ──
        var streaksEnabled = _cvEnableKillStreaks is not { } ks || ks.GetBool();

        // Primary announcement — mutually exclusive, like the original if/else chain.
        if (!_firstBloodOccurred && (_cvEnableFirstBlood is not { } fb || fb.GetBool()))
        {
            _firstBloodOccurred = true;
            QueueSound("firstblood", PrioFirstBlood, invoker: killerClient);
        }
        else if (IsKnife(death.Weapon))
        {
            QueueSound("humiliation", PrioHumiliation, invoker: killerClient);
        }
        else if (IsGrenade(death.Weapon))
        {
            QueueSound("excellent", PrioExcellent, invoker: killerClient);
        }
        else if (streaksEnabled && ComboTiers.TryGetValue(_comboCount[slot], out var comboKey))
        {
            QueueSound(comboKey, PrioComboBase + _comboCount[slot], invoker: killerClient);
        }
        else if (streaksEnabled && KillstreakTiers.TryGetValue(_killStreaks[slot], out var streakKey))
        {
            QueueSound(streakKey, PrioKillstreakBase + _killStreaks[slot], invoker: killerClient);
        }

        if (death.Headshot && (_cvEnableHeadshot is not { } hs || hs.GetBool()))
        {
            if (HeadshotTiers.TryGetValue(_headshotStreak[slot], out var hsKey))
                QueueSound(hsKey, PrioHeadshotBase + _headshotStreak[slot], invoker: killerClient);
            else
                QueueSound("headshot", PrioHeadshotDing, invoker: killerClient, personal: killerClient);
        }
    }

    // Sound priorities — higher wins when several land in the same coalescing window. Killstreak
    // magnitude is meant to dominate (a 60-streak flawlessvictory beats anything); the base
    // personal headshot ding is the quietest, so any real announcement overrides it.
    private const int PrioHeadshotDing   = 5;
    private const int PrioPancake        = 8;
    private const int PrioTeamkill       = 8;
    private const int PrioExcellent      = 12;
    private const int PrioHumiliation    = 14;
    private const int PrioComboBase      = 20;  // + comboCount  (double=22 … comboking=27)
    private const int PrioHeadshotBase   = 28;  // + hsStreak    (headhunter=31 … outstanding=39)
    private const int PrioFirstBlood     = 50;
    private const int PrioKillstreakBase = 40;  // + killStreak  (dominating=44 … flawlessvictory=100)

    private static bool IsKnife(string weapon)
        => weapon.Contains("knife", StringComparison.OrdinalIgnoreCase)
           || weapon.Contains("bayonet", StringComparison.OrdinalIgnoreCase);

    private static bool IsGrenade(string weapon)
        => weapon.Contains("hegrenade", StringComparison.OrdinalIgnoreCase)
           || weapon.Contains("molotov",  StringComparison.OrdinalIgnoreCase)
           || weapon.Contains("inferno",  StringComparison.OrdinalIgnoreCase)
           || weapon.Contains("incgrenade", StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Settings Menu

    private ECommandAction OnQuakeMenuCommand(IGameClient client, StringCommand command)
    {
        if (client.IsFakeClient)
            return ECommandAction.Skipped;

        if (_menuManager is null)
        {
            client.Print(HudPrintChannel.Chat, " [QuakeSounds] Menu is not available.");
            return ECommandAction.Handled;
        }

        var menu = BuildMainMenu();
        _menuManager.DisplayMenu(client, menu);
        return ECommandAction.Handled;
    }

    private Menu BuildMainMenu()
    {
        return Menu.Create()
            .Title(client => _localizerManager?.For(client).Text("quakesounds.menu.title") ?? "QuakeSounds Settings")
            .SubMenu(
                client =>
                {
                    var pack = _soundPackManager.GetPlayerPack(client.Slot);
                    return _localizerManager?.For(client).Text("quakesounds.menu.soundpack", pack.Name)
                           ?? $"Sound Pack: {pack.Name}";
                },
                BuildPackSubMenu)
            .SubMenu(
                client =>
                {
                    var vol = GetPlayerVolume(client.Slot);
                    var pct = $"{(int)(vol * 100)}%";
                    return _localizerManager?.For(client).Text("quakesounds.menu.volume", pct)
                           ?? $"Volume: {pct}";
                },
                BuildVolumeSubMenu)
            .Item(
                client =>
                {
                    var muted = IsPlayerMuted(client.Slot);
                    var state = muted
                        ? (_localizerManager?.For(client).Text("quakesounds.menu.on") ?? "ON")
                        : (_localizerManager?.For(client).Text("quakesounds.menu.off") ?? "OFF");
                    return _localizerManager?.For(client).Text("quakesounds.menu.mute", state)
                           ?? $"Mute: {state}";
                },
                ctrl =>
                {
                    ToggleMute(ctrl.Client);
                    ctrl.Refresh();
                })
            .ExitItem(client => _localizerManager?.For(client).Text("quakesounds.menu.close") ?? "Close")
            .Build();
    }

    private Menu BuildPackSubMenu(IGameClient client)
    {
        var available = _soundPackManager.GetAvailablePacks(client);
        var currentPack = _soundPackManager.GetPlayerPack(client.Slot);

        var builder = Menu.Create()
            .Title(c => _localizerManager?.For(c).Text("quakesounds.menu.select_pack") ?? "Select Sound Pack");

        foreach (var pack in available)
        {
            var packId = pack.Id;
            var packName = pack.Name;
            var isCurrent = pack.Id == currentPack.Id;
            var display = isCurrent ? $"\u2713 {packName}" : packName;

            builder.Item(display, ctrl =>
            {
                _soundPackManager.SetPlayerPack(ctrl.Client, packId);

                var selectedMsg = _localizerManager?.For(ctrl.Client).Text("quakesounds.pack.selected", packName)
                                  ?? $"Sound pack changed to: {packName}";
                ctrl.Client.Print(HudPrintChannel.Chat, $" [QuakeSounds] {selectedMsg}");

                ctrl.GoBack();
            });
        }

        builder.BackItem(c => _localizerManager?.For(c).Text("quakesounds.menu.back") ?? "Back");

        return builder.Build();
    }

    private Menu BuildVolumeSubMenu(IGameClient client)
    {
        var currentVol = GetPlayerVolume(client.Slot);

        var builder = Menu.Create()
            .Title(c => _localizerManager?.For(c).Text("quakesounds.menu.volume_title") ?? "Volume");

        foreach (var (value, label) in VolumePresets)
        {
            var presetValue = value;
            var presetLabel = label;
            var isCurrent = Math.Abs(currentVol - presetValue) < 0.01f;
            var display = isCurrent ? $"\u2713 {presetLabel}" : presetLabel;

            builder.Item(display, ctrl =>
            {
                SetPlayerVolume(ctrl.Client, presetValue);

                var volMsg = _localizerManager?.For(ctrl.Client).Text("quakesounds.volume.changed", presetLabel)
                             ?? $"Volume set to {presetLabel}";
                ctrl.Client.Print(HudPrintChannel.Chat, $" [QuakeSounds] {volMsg}");

                ctrl.GoBack();
            });
        }

        builder.BackItem(c => _localizerManager?.For(c).Text("quakesounds.menu.back") ?? "Back");

        return builder.Build();
    }

    #endregion

    #region Sound Pack Command

    private ECommandAction OnQuakePackCommand(IGameClient client, StringCommand command)
    {
        if (client.IsFakeClient)
            return ECommandAction.Skipped;

        var available = _soundPackManager.GetAvailablePacks(client);
        if (available.Count == 0)
        {
            client.Print(HudPrintChannel.Chat, " [QuakeSounds] No sound packs available.");
            return ECommandAction.Handled;
        }

        var currentPack = _soundPackManager.GetPlayerPack(client.Slot);

        // Find current pack index in available list and cycle to next
        var currentIdx = available.FindIndex(p => p.Id == currentPack.Id);
        var nextIdx = (currentIdx + 1) % available.Count;
        var nextPack = available[nextIdx];

        if (nextPack.Id == currentPack.Id && available.Count == 1)
        {
            // Only one pack available, show current
            var currentMsg = _localizerManager?.For(client).Text("quakesounds.pack.current", currentPack.Name)
                             ?? $"Current sound pack: {currentPack.Name}";
            client.Print(HudPrintChannel.Chat, $" [QuakeSounds] {currentMsg}");
        }
        else
        {
            _soundPackManager.SetPlayerPack(client, nextPack.Id);

            var selectedMsg = _localizerManager?.For(client).Text("quakesounds.pack.selected", nextPack.Name)
                              ?? $"Sound pack changed to: {nextPack.Name}";
            client.Print(HudPrintChannel.Chat, $" [QuakeSounds] {selectedMsg}");
        }

        return ECommandAction.Handled;
    }

    #endregion

    #region Mute Preference

    private void LoadMutePreference(IGameClient client)
    {
        if (_clientPreference is null)
        {
            _muteCache[client.Slot] = false;
            return;
        }

        if (!_clientPreference.IsLoaded(client.SteamId))
        {
            _muteCache[client.Slot] = false;
            return;
        }

        var cookie = _clientPreference.GetCookie(client.SteamId, MuteCookieKey);
        _muteCache[client.Slot] = cookie is { Type: CookieValueType.Number } && cookie.GetNumber() == 1;
    }

    private bool IsPlayerMuted(int slot) => _muteCache[slot] == true;

    private void ToggleMute(IGameClient client)
    {
        var muted = !IsPlayerMuted(client.Slot);
        _muteCache[client.Slot] = muted;
        _clientPreference?.SetCookie(client.SteamId, MuteCookieKey, muted ? 1L : 0L);

        var msg = _localizerManager?.For(client).Text(muted ? "quakesounds.muted" : "quakesounds.unmuted")
                  ?? (muted ? "Quake sounds muted." : "Quake sounds unmuted.");

        client.Print(HudPrintChannel.Chat, $" [QuakeSounds] {msg}");
    }

    private ECommandAction OnQuakeMuteCommand(IGameClient client, StringCommand command)
    {
        if (client.IsFakeClient)
            return ECommandAction.Skipped;

        ToggleMute(client);
        return ECommandAction.Handled;
    }

    #endregion

    #region Volume Preference

    private void LoadVolumePreference(IGameClient client)
    {
        if (_clientPreference is null || !_clientPreference.IsLoaded(client.SteamId))
        {
            _volumeCache[client.Slot] = null;
            return;
        }

        var cookie = _clientPreference.GetCookie(client.SteamId, VolumeCookieKey);

        if (cookie is { Type: CookieValueType.Double })
        {
            _volumeCache[client.Slot] = (float)cookie.GetDouble();
        }
        else
        {
            _volumeCache[client.Slot] = null;
        }
    }

    private float GetPlayerVolume(int slot)
    {
        return _volumeCache[slot] ?? _cvVolume?.GetFloat() ?? 1.0f;
    }

    private void SetPlayerVolume(IGameClient client, float volume)
    {
        _volumeCache[client.Slot] = volume;
        _clientPreference?.SetCookie(client.SteamId, VolumeCookieKey, (double)volume);
    }

    #endregion

    #region Sound Playback

    private void QueueSound(string soundKey, int priority, IGameClient? invoker = null, IGameClient? personal = null)
    {
        if (string.IsNullOrEmpty(soundKey))
            return;

        if (priority > _pendingPriority)
        {
            _pendingPriority = priority;
            _pendingSoundKey = soundKey;
            _pendingClient   = personal;
            _pendingInvoker  = invoker;
        }

        if (_pendingScheduled)
            return;

        _pendingScheduled = true;
        var delay = System.Math.Max(0f, _cvSoundDebounce?.GetFloat() ?? 0.1f);
        _bridge.ModSharp.PushTimer(FlushPendingSound, delay);
    }

    private void FlushPendingSound()
    {
        var key     = _pendingSoundKey;
        var client  = _pendingClient;
        var invoker = _pendingInvoker;

        _pendingPriority  = int.MinValue;
        _pendingSoundKey  = null;
        _pendingClient    = null;
        _pendingInvoker   = null;
        _pendingScheduled = false;

        if (string.IsNullOrEmpty(key))
            return;

        if (client is null)
            PlaySoundToAll(key, invoker);
        else if (client.IsInGame)
            PlaySoundToPlayer(client, key, invoker);
    }

    private void PlaySoundToAll(string soundKey, IGameClient? invoker)
    {
        string? sharedEvent = null;
        if (invoker is { IsInGame: true })
            sharedEvent = _soundPackManager.GetPlayerPack(invoker.Slot).GetSound(soundKey);

        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (client.IsFakeClient || client.IsHltv)
                continue;
            if (IsPlayerMuted(client.Slot))
                continue;

            var controller = client.GetPlayerController();
            if (controller is not { IsValidEntity: true })
                continue;

            var soundEvent = sharedEvent ?? _soundPackManager.GetPlayerPack(client.Slot).GetSound(soundKey);
            if (string.IsNullOrEmpty(soundEvent))
                continue;

            controller.EmitSoundClient(soundEvent, GetPlayerVolume(client.Slot));
        }
    }

    private void PlaySoundToPlayer(IGameClient client, string soundKey, IGameClient? invoker)
    {
        var controller = client.GetPlayerController();
        if (controller is not { IsValidEntity: true })
            return;

        var packOwner = invoker is { IsInGame: true } ? invoker : client;
        var soundEvent = _soundPackManager.GetPlayerPack(packOwner.Slot).GetSound(soundKey);
        if (string.IsNullOrEmpty(soundEvent))
            return;

        controller.EmitSoundClient(soundEvent, GetPlayerVolume(client.Slot));
    }

    #endregion

    #region Center HTML Messages (survival token)

    private void ShowLocalizedCenterMessageToAll(string playerName, string streakLocaleKey, string color)
    {
        if (_cvEnableCenterMsg is { } cm && !cm.GetBool())
            return;

        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (client.IsFakeClient || client.IsHltv)
                continue;
            if (IsPlayerMuted(client.Slot))
                continue;

            var streakName = _localizerManager?.For(client).Text(streakLocaleKey) ?? streakLocaleKey;

            // Use string.Format manually — .Text() with format args calls Format() which
            // throws if the locale string has placeholders but args are passed to Text() incorrectly
            string message;
            try
            {
                message = _localizerManager?.For(client).Text("quakesounds.streak_message", playerName, streakName)
                          ?? $"{playerName} — {streakName}!";
            }
            catch
            {
                message = $"{playerName} — {streakName}!";
            }

            var html = $"<font color='{color}'><b>{message}</b></font>";
            PrintCenterHtml(client, html);
        }
    }

    private void PrintCenterHtml(IGameClient client, string html, int duration = 3)
    {
        var e = _bridge.EventManager.CreateEvent("show_survival_respawn_status", true);
        if (e is null)
            return;

        e.SetString("loc_token", html);
        e.SetInt("duration", duration);
        e.SetInt("userid", client.UserId);
        e.FireToClient(client);
        e.Dispose();
    }

    #endregion
}
