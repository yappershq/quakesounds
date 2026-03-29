using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Modules.LocalizerManager.Shared;
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

    // Per-player kill streak tracking (slot-indexed)
    private readonly int[] _killStreaks = new int[64];
    private bool _firstBloodOccurred;

    // ConVars
    private IConVar? _cvEnabled;
    private IConVar? _cvEnableKillStreaks;
    private IConVar? _cvEnableFirstBlood;
    private IConVar? _cvEnableHeadshot;
    private IConVar? _cvEnableCenterMsg;
    private IConVar? _cvEnableDuringWarmup;
    private IConVar? _cvResetOnDeath;

    // Cached module interfaces
    private IClientPreference? _clientPreference;
    private ILocalizerManager? _localizerManager;

    private const string MuteCookieKey = "quakesounds_muted";
    private readonly bool?[] _muteCache = new bool?[64];

    // Forward delegate reference (for uninstall)
    private Action<IPlayerKilledForwardParams>? _playerKilledForward;

    // Sound mappings
    private static readonly (string sound, string name, string color)[] StreakData =
    [
        ("", "", ""),                                                    // 0 - unused
        ("", "", ""),                                                    // 1 - unused
        ("crafting.killstreaks.doublekill",      "DOUBLE KILL",    "#FFFFFF"), // 2
        ("crafting.killstreaks.3_killingspree",  "KILLING SPREE",  "#00FF00"), // 3
        ("crafting.killstreaks.4_dominating",    "DOMINATING",      "#FFFF00"), // 4
        ("crafting.killstreaks.5_holyshit",      "HOLY SHIT",       "#FF8800"), // 5
        ("crafting.killstreaks.6_wickedsick",    "WICKED SICK",     "#FF4400"), // 6
        ("crafting.killstreaks.7_monsterkill",   "MONSTER KILL",    "#FF0000"), // 7
        ("crafting.killstreaks.8_unstoppable",   "UNSTOPPABLE",     "#FF00FF"), // 8
        ("crafting.killstreaks.9_godlike",       "GODLIKE",         "#FF0000"), // 9
    ];

    public QuakeSoundsModule(InterfaceBridge bridge, ILogger<QuakeSoundsModule> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public bool Init()
    {
        _cvEnabled           = _bridge.ConVarManager.CreateConVar("qs_enabled",        true,  "Enable QuakeSounds plugin");
        _cvEnableKillStreaks  = _bridge.ConVarManager.CreateConVar("qs_killstreaks",    true,  "Enable kill streak sounds");
        _cvEnableFirstBlood   = _bridge.ConVarManager.CreateConVar("qs_firstblood",     true,  "Enable first blood sound");
        _cvEnableHeadshot     = _bridge.ConVarManager.CreateConVar("qs_headshot",       true,  "Enable headshot kill sound");
        _cvEnableCenterMsg    = _bridge.ConVarManager.CreateConVar("qs_center_message", true,  "Enable center HTML messages");
        _cvEnableDuringWarmup = _bridge.ConVarManager.CreateConVar("qs_during_warmup",  false, "Enable sounds during warmup");
        _cvResetOnDeath       = _bridge.ConVarManager.CreateConVar("qs_reset_on_death", false, "Reset streak on death");

        _bridge.ModSharp.InstallGameListener(this);
        _bridge.ClientManager.InstallClientListener(this);

        // Use PlayerKilledPost forward instead of game event
        _playerKilledForward = OnPlayerKilledPost;
        _bridge.HookManager.PlayerKilledPost.InstallForward(_playerKilledForward);

        _bridge.ClientManager.InstallCommandCallback("quakemute", OnQuakeMuteCommand);

        _logger.LogInformation("[QuakeSounds] Initialized");
        return true;
    }

    public void OnPostInit(ServiceProvider provider)
    {
        _clientPreference = _bridge.GetClientPreference();
        _localizerManager = _bridge.GetLocalizerManager();

        if (_clientPreference is null)
            _logger.LogWarning("[QuakeSounds] ClientPreference not available — mute won't persist");
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);
        _bridge.ClientManager.RemoveClientListener(this);

        if (_playerKilledForward is not null)
            _bridge.HookManager.PlayerKilledPost.RemoveForward(_playerKilledForward);

        _bridge.ClientManager.RemoveCommandCallback("quakemute", OnQuakeMuteCommand);

        Array.Clear(_killStreaks);
        Array.Clear(_muteCache);
    }

    #region IGameListener

    public int ListenerPriority => 0;
    public int ListenerVersion  => IGameListener.ApiVersion;

    public void OnRoundRestarted()
    {
        Array.Clear(_killStreaks);
        _firstBloodOccurred = false;
    }

    #endregion

    #region IClientListener

    int IClientListener.ListenerPriority => 0;
    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;

    public void OnClientPutInServer(IGameClient client)
    {
        if (client.IsFakeClient)
            return;

        _killStreaks[client.Slot] = 0;
        LoadMutePreference(client);
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        _killStreaks[client.Slot] = 0;
        _muteCache[client.Slot]  = null;
    }

    #endregion

    #region Kill Handling (PlayerKilledPost forward)

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
            _killStreaks[victimClient.Slot] = 0;

        // Get attacker slot
        var attackerSlot = @params.AttackerPlayerSlot;
        if (attackerSlot < 0)
            return;

        // Self-kill check
        if (attackerSlot == victimClient.Slot)
            return;

        var killerClient = _bridge.ClientManager.GetGameClient(new PlayerSlot((byte)attackerSlot));
        if (killerClient is not { IsInGame: true })
            return;

        var killerController = killerClient.GetPlayerController();
        if (killerController is null)
            return;

        var killerName = killerController.PlayerName ?? $"Player {attackerSlot}";

        // Increment streak
        _killStreaks[attackerSlot]++;
        var kills = _killStreaks[attackerSlot];

        var soundPlayed = false;

        // First blood
        if (!_firstBloodOccurred && _cvEnableFirstBlood is { } fb && fb.GetBool())
        {
            _firstBloodOccurred = true;
            PlaySoundToAll("kills.firstblood");
            ShowCenterMessageToAll(killerName, "FIRST BLOOD", "#FF0000");
            soundPlayed = true;
        }

        // Kill streak
        if (!soundPlayed && _cvEnableKillStreaks is { } ks && ks.GetBool())
        {
            var idx = Math.Min(kills, 9);
            if (idx >= 2)
            {
                var (sound, name, color) = StreakData[idx];
                PlaySoundToAll(sound);

                if (_cvEnableCenterMsg is { } cm && cm.GetBool())
                    ShowCenterMessageToAll(killerName, name, color);

                soundPlayed = true;
            }
        }

        // Headshot (killer only)
        var isHeadshot = @params.HitGroup == HitGroupType.Head
                         || (@params.DamageType & DamageFlagBits.Headshot) != 0;

        if (isHeadshot && _cvEnableHeadshot is { } hs && hs.GetBool())
            PlaySoundToPlayer(killerClient, "effects.hitmark.headshot");
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

    #region Sound Playback

    private void PlaySoundToAll(string soundEvent)
    {
        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (client.IsFakeClient || client.IsHltv)
                continue;
            if (IsPlayerMuted(client.Slot))
                continue;

            var pawn = client.GetPlayerController()?.GetPlayerPawn();
            if (pawn is not { IsValidEntity: true })
                continue;

            pawn.EmitSoundClient(soundEvent);
        }
    }

    private static void PlaySoundToPlayer(IGameClient client, string soundEvent)
    {
        var pawn = client.GetPlayerController()?.GetPlayerPawn();
        if (pawn is not { IsValidEntity: true })
            return;

        pawn.EmitSoundClient(soundEvent);
    }

    #endregion

    #region Center HTML Messages (survival token)

    private void ShowCenterMessageToAll(string playerName, string streakName, string color)
    {
        if (_cvEnableCenterMsg is { } cm && !cm.GetBool())
            return;

        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (client.IsFakeClient || client.IsHltv)
                continue;
            if (IsPlayerMuted(client.Slot))
                continue;

            var html = BuildCenterHtml(client, playerName, streakName, color);
            PrintCenterHtml(client, html);
        }
    }

    private string BuildCenterHtml(IGameClient client, string playerName, string streakName, string color)
    {
        var localized = _localizerManager?.For(client).Text("quakesounds.streak_message");

        if (localized is not null)
        {
            var formatted = string.Format(localized, playerName, streakName);
            return $"<font color='{color}'><b>{formatted}</b></font>";
        }

        return $"<font color='{color}'><b>{playerName} — {streakName}!</b></font>";
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
