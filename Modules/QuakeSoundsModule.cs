using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuakeSounds.Managers;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace QuakeSounds.Modules;

internal sealed class QuakeSoundsModule : IModule, IGameListener, IClientListener
{
    private readonly InterfaceBridge            _bridge;
    private readonly ILogger<QuakeSoundsModule> _logger;
    private readonly SoundPackManager           _soundPackManager;

    // Per-player tracking (slot-indexed)
    private readonly int[]    _killStreaks    = new int[64];
    private readonly double[] _lastKillTime  = new double[64]; // engine time of last kill
    private readonly int[]    _rapidKills    = new int[64];    // rapid kill count (within time window)
    private bool _firstBloodOccurred;

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

    // Cached module interfaces
    private IClientPreference? _clientPreference;
    private ILocalizerManager? _localizerManager;
    private IMenuManager?      _menuManager;

    private const string MuteCookieKey = "quakesounds_muted";
    private readonly bool?[] _muteCache = new bool?[64];

    private Action<IPlayerKilledForwardParams>? _playerKilledForward;

    // Volume presets
    private static readonly (float value, string label)[] VolumePresets =
    [
        (1.00f, "100%"),
        (0.75f, "75%"),
        (0.50f, "50%"),
        (0.25f, "25%"),
        (0.10f, "10%"),
    ];

    // Streak key mapping: kill count -> sound pack key and locale info
    private static readonly (string key, string localeKey, string color)[] StreakMap =
    [
        ("", "", ""),                                                           // 0
        ("", "", ""),                                                           // 1
        ("doublekill",   "quakesounds.streak.doublekill",   "#FFFFFF"), // 2
        ("killingspree", "quakesounds.streak.killingspree", "#00FF00"), // 3
        ("dominating",   "quakesounds.streak.dominating",   "#FFFF00"), // 4
        ("holyshit",     "quakesounds.streak.holyshit",     "#FF8800"), // 5
        ("wickedsick",   "quakesounds.streak.wickedsick",   "#FF4400"), // 6
        ("monsterkill",  "quakesounds.streak.monsterkill",  "#FF0000"), // 7
        ("unstoppable",  "quakesounds.streak.unstoppable",  "#FF00FF"), // 8
        ("godlike",      "quakesounds.streak.godlike",      "#FF0000"), // 9
    ];

    // Rapid multi-kill key mapping: rapid count -> sound pack key and locale info
    private static readonly (string key, string localeKey, string color)[] RapidMap =
    [
        ("", "", ""),                                                           // 0
        ("", "", ""),                                                           // 1
        ("", "", ""),                                                           // 2
        ("triplekill",  "quakesounds.rapid.triplekill",  "#00FFFF"), // 3
        ("quadrakill",  "quakesounds.rapid.quadrakill",  "#FF00FF"), // 4
        ("pentakill",   "quakesounds.rapid.pentakill",   "#FFD700"), // 5
    ];

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

        _bridge.ModSharp.InstallGameListener(this);
        _bridge.ClientManager.InstallClientListener(this);

        _playerKilledForward = OnPlayerKilledPost;
        _bridge.HookManager.PlayerKilledPost.InstallForward(_playerKilledForward);

        _bridge.ClientManager.InstallCommandCallback("quakemute", OnQuakeMuteCommand);
        _bridge.ClientManager.InstallCommandCallback("quakepack", OnQuakePackCommand);
        _bridge.ClientManager.InstallCommandCallback("quake", OnQuakeMenuCommand);
        _bridge.ClientManager.InstallCommandCallback("qs", OnQuakeMenuCommand);

        _logger.LogInformation("[QuakeSounds] Initialized");
        return true;
    }

    public void OnPostInit(ServiceProvider provider)
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

        if (_playerKilledForward is not null)
            _bridge.HookManager.PlayerKilledPost.RemoveForward(_playerKilledForward);

        _bridge.ClientManager.RemoveCommandCallback("quakemute", OnQuakeMuteCommand);
        _bridge.ClientManager.RemoveCommandCallback("quakepack", OnQuakePackCommand);
        _bridge.ClientManager.RemoveCommandCallback("quake", OnQuakeMenuCommand);
        _bridge.ClientManager.RemoveCommandCallback("qs", OnQuakeMenuCommand);

        Array.Clear(_killStreaks);
        Array.Clear(_lastKillTime);
        Array.Clear(_rapidKills);
        Array.Clear(_muteCache);
        Array.Clear(_volumeCache);
    }

    #region IGameListener

    public int ListenerPriority => 0;
    public int ListenerVersion  => IGameListener.ApiVersion;

    public void OnRoundRestarted()
    {
        Array.Clear(_killStreaks);
        Array.Clear(_lastKillTime);
        Array.Clear(_rapidKills);
        _firstBloodOccurred = false;
    }

    #endregion

    #region IClientListener

    int IClientListener.ListenerPriority => 0;
    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;

    public void OnClientPutInServer(IGameClient client)
    {
        if (client.IsFakeClient) return;

        _killStreaks[client.Slot]   = 0;
        _lastKillTime[client.Slot] = 0;
        _rapidKills[client.Slot]   = 0;
        LoadMutePreference(client);
        LoadVolumePreference(client);
        _soundPackManager.LoadPlayerPack(client);
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        _killStreaks[client.Slot]   = 0;
        _lastKillTime[client.Slot] = 0;
        _rapidKills[client.Slot]   = 0;
        _muteCache[client.Slot]    = null;
        _volumeCache[client.Slot]  = null;
        _soundPackManager.ClearPlayerCache(client.Slot);
    }

    #endregion

    #region Kill Handling

    private void OnPlayerKilledPost(IPlayerKilledForwardParams @params)
    {
        if (_cvEnabled is { } cv && !cv.GetBool())
            return;

        if (_cvEnableDuringWarmup is { } wc && !wc.GetBool() && _bridge.GameRules.IsWarmupPeriod)
            return;

        var victimClient = @params.Client;
        if (victimClient is not { IsInGame: true })
            return;

        // Reset victim streak on death
        if (_cvResetOnDeath is { } rd && rd.GetBool())
        {
            _killStreaks[victimClient.Slot]  = 0;
            _rapidKills[victimClient.Slot]   = 0;
        }

        var attackerSlot = @params.AttackerPlayerSlot;
        if (attackerSlot < 0 || attackerSlot == victimClient.Slot)
            return;

        var killerClient = _bridge.ClientManager.GetGameClient(new PlayerSlot((byte)attackerSlot));
        if (killerClient is not { IsInGame: true })
            return;

        var killerController = killerClient.GetPlayerController();
        if (killerController is null)
            return;

        var killerName = killerController.PlayerName ?? $"Player {attackerSlot}";
        var now = _bridge.ModSharp.EngineTime();

        // Increment streak
        _killStreaks[attackerSlot]++;
        var kills = _killStreaks[attackerSlot];

        // Track rapid kills (kills within time window)
        var rapidWindow = _cvRapidKillWindow?.GetFloat() ?? 4f;
        if (now - _lastKillTime[attackerSlot] <= rapidWindow)
            _rapidKills[attackerSlot]++;
        else
            _rapidKills[attackerSlot] = 1;

        _lastKillTime[attackerSlot] = now;

        var soundPlayed = false;

        // First blood (highest priority)
        if (!_firstBloodOccurred && _cvEnableFirstBlood is { } fb && fb.GetBool())
        {
            _firstBloodOccurred = true;
            PlaySoundToAll("firstblood");
            ShowLocalizedCenterMessageToAll(killerName, "quakesounds.streak.firstblood", "#FF0000");
            soundPlayed = true;
        }

        // Rapid multi-kill (triple/quadra/penta -- overrides streak sound)
        if (!soundPlayed && _rapidKills[attackerSlot] >= 3 && _cvEnableKillStreaks is { } rk && rk.GetBool())
        {
            var rapidIdx = Math.Min(_rapidKills[attackerSlot], 5);
            var (rapidKey, rapidLocale, rapidColor) = RapidMap[rapidIdx];

            if (!string.IsNullOrEmpty(rapidKey))
            {
                PlaySoundToAll(rapidKey);

                if (_cvEnableCenterMsg is { } cm && cm.GetBool())
                    ShowLocalizedCenterMessageToAll(killerName, rapidLocale, rapidColor);

                soundPlayed = true;
            }
        }

        // Kill streak (2-9+)
        if (!soundPlayed && _cvEnableKillStreaks is { } ks && ks.GetBool())
        {
            var idx = Math.Min(kills, 9);
            if (idx >= 2)
            {
                var (streakKey, localeKey, color) = StreakMap[idx];
                PlaySoundToAll(streakKey);

                if (_cvEnableCenterMsg is { } cm && cm.GetBool())
                    ShowLocalizedCenterMessageToAll(killerName, localeKey, color);

                soundPlayed = true;
            }
        }

        // Headshot (killer only, independent of other sounds)
        var isHeadshot = @params.HitGroup == HitGroupType.Head
                         || (@params.DamageType & DamageFlagBits.Headshot) != 0;

        if (isHeadshot && _cvEnableHeadshot is { } hs && hs.GetBool())
            PlaySoundToPlayer(killerClient, "headshot");
    }

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
                    var label = _localizerManager?.For(client).Text("quakesounds.menu.soundpack") ?? "Sound Pack: {0}";
                    return string.Format(label, pack.Name);
                },
                BuildPackSubMenu)
            .SubMenu(
                client =>
                {
                    var vol = GetPlayerVolume(client.Slot);
                    var pct = $"{(int)(vol * 100)}%";
                    var label = _localizerManager?.For(client).Text("quakesounds.menu.volume") ?? "Volume: {0}";
                    return string.Format(label, pct);
                },
                BuildVolumeSubMenu)
            .Item(
                client =>
                {
                    var muted = IsPlayerMuted(client.Slot);
                    var label = _localizerManager?.For(client).Text("quakesounds.menu.mute") ?? "Mute: {0}";
                    var state = muted
                        ? (_localizerManager?.For(client).Text("quakesounds.menu.on") ?? "ON")
                        : (_localizerManager?.For(client).Text("quakesounds.menu.off") ?? "OFF");
                    return string.Format(label, state);
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

                var selectedMsg = _localizerManager?.For(ctrl.Client).Text("quakesounds.pack.selected")
                                  ?? "Sound pack changed to: {0}";
                ctrl.Client.Print(HudPrintChannel.Chat, $" [QuakeSounds] {string.Format(selectedMsg, packName)}");

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

                var volMsg = _localizerManager?.For(ctrl.Client).Text("quakesounds.volume.changed")
                             ?? "Volume set to {0}";
                ctrl.Client.Print(HudPrintChannel.Chat, $" [QuakeSounds] {string.Format(volMsg, presetLabel)}");

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
            var currentMsg = _localizerManager?.For(client).Text("quakesounds.pack.current")
                             ?? "Current sound pack: {0}";
            client.Print(HudPrintChannel.Chat, $" [QuakeSounds] {string.Format(currentMsg, currentPack.Name)}");
        }
        else
        {
            _soundPackManager.SetPlayerPack(client, nextPack.Id);

            var selectedMsg = _localizerManager?.For(client).Text("quakesounds.pack.selected")
                              ?? "Sound pack changed to: {0}";
            client.Print(HudPrintChannel.Chat, $" [QuakeSounds] {string.Format(selectedMsg, nextPack.Name)}");
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

    private void PlaySoundToAll(string soundKey)
    {
        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (client.IsFakeClient || client.IsHltv)
                continue;
            if (IsPlayerMuted(client.Slot))
                continue;

            var controller = client.GetPlayerController();
            if (controller is not { IsValidEntity: true })
                continue;

            var pawn = controller.GetPlayerPawn();
            if (pawn is not { IsValidEntity: true, IsAlive: true })
                continue;

            // Resolve sound from the listener's selected pack
            var pack = _soundPackManager.GetPlayerPack(client.Slot);
            var soundEvent = pack.GetSound(soundKey);

            if (!string.IsNullOrEmpty(soundEvent))
            {
                var volume = GetPlayerVolume(client.Slot);
                pawn.EmitSoundClient(soundEvent, volume);
            }
        }
    }

    private void PlaySoundToPlayer(IGameClient client, string soundKey)
    {
        var controller = client.GetPlayerController();
        if (controller is not { IsValidEntity: true })
            return;

        var pawn = controller.GetPlayerPawn();
        if (pawn is not { IsValidEntity: true, IsAlive: true })
            return;

        // Resolve sound from the player's selected pack
        var pack = _soundPackManager.GetPlayerPack(client.Slot);
        var soundEvent = pack.GetSound(soundKey);

        if (!string.IsNullOrEmpty(soundEvent))
        {
            var volume = GetPlayerVolume(client.Slot);
            pawn.EmitSoundClient(soundEvent, volume);
        }
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
