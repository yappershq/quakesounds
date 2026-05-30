using System.Text.Json;
using Microsoft.Extensions.Logging;
using QuakeSounds.Models;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Shared.Objects;

namespace QuakeSounds.Managers;

internal sealed class SoundPackManager
{
    private readonly InterfaceBridge            _bridge;
    private readonly ILogger<SoundPackManager>  _logger;

    private List<SoundPack> _packs = [];
    private SoundPack       _defaultPack = new() { Id = "default", Name = "Default" };

    private List<string> _precacheFiles = [];

    // Where the quake.* soundevents live in the mounted workshop addon (cstema assets).
    private const string DefaultSoundEventFile = "soundevents/soundevents_general.vsndevts";

    private const string PackCookieKey = "quakesounds_pack";

    /// <summary>Soundevent files to precache (resolved from config, never empty after LoadPacks).</summary>
    public IReadOnlyList<string> PrecacheFiles => _precacheFiles;

    // Per-player selected pack id cache (slot-indexed)
    private readonly string?[] _packCache = new string?[64];

    public SoundPackManager(InterfaceBridge bridge, ILogger<SoundPackManager> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public void LoadPacks(string sharpPath)
    {
        var configPath = Path.Combine(sharpPath, "configs", "quakesounds.json");

        if (!File.Exists(configPath))
        {
            _logger.LogWarning("[QuakeSounds] quakesounds.json not found at {Path}, creating default", configPath);
            CreateDefaultConfig(configPath);
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<SoundPackConfig>(json);

            if (config?.Packs is { Count: > 0 })
            {
                _packs = config.Packs;
                _defaultPack = _packs.Find(p => p.Id == "default") ?? _packs[0];
                _logger.LogInformation("[QuakeSounds] Loaded {Count} sound pack(s)", _packs.Count);
            }
            else
            {
                _logger.LogWarning("[QuakeSounds] No packs found in config, using built-in default");
            }

            // Resolve precache list; fall back to the default soundevent file when omitted so an
            // older config (no precacheFiles key) still registers the quake.* events.
            _precacheFiles = config?.PrecacheFiles is { Count: > 0 }
                ? config.PrecacheFiles.Where(f => !string.IsNullOrWhiteSpace(f)).ToList()
                : [DefaultSoundEventFile];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[QuakeSounds] Failed to load quakesounds.json");
        }
    }

    public List<SoundPack> GetAvailablePacks(IGameClient client)
    {
        var adminManager = _bridge.GetAdminManager();
        var result = new List<SoundPack>();

        foreach (var pack in _packs)
        {
            if (string.IsNullOrEmpty(pack.Permission))
            {
                result.Add(pack);
                continue;
            }

            if (adminManager is null)
                continue;

            var admin = adminManager.GetAdmin(client.SteamId);
            if (admin is not null && admin.HasPermission(pack.Permission))
                result.Add(pack);
        }

        return result;
    }

    public SoundPack DefaultPack => _defaultPack;

    public SoundPack GetPlayerPack(int slot)
    {
        var packId = _packCache[slot];

        if (packId is null)
            return _defaultPack;

        var pack = _packs.Find(p => p.Id == packId);
        return pack ?? _defaultPack;
    }

    public void SetPlayerPack(IGameClient client, string packId)
    {
        _packCache[client.Slot] = packId;

        var clientPreference = _bridge.GetClientPreference();
        clientPreference?.SetCookie(client.SteamId, PackCookieKey, packId);
    }

    public void LoadPlayerPack(IGameClient client)
    {
        var clientPreference = _bridge.GetClientPreference();

        if (clientPreference is null || !clientPreference.IsLoaded(client.SteamId))
        {
            _packCache[client.Slot] = null;
            return;
        }

        var cookie = clientPreference.GetCookie(client.SteamId, PackCookieKey);

        if (cookie is { Type: CookieValueType.String })
        {
            var packId = cookie.GetString();

            // Validate that the pack exists; if not, fall back to default
            var pack = _packs.Find(p => p.Id == packId);
            _packCache[client.Slot] = pack is not null ? packId : null;
        }
        else
        {
            _packCache[client.Slot] = null;
        }
    }

    public void ClearPlayerCache(int slot)
    {
        _packCache[slot] = null;
    }

    private static void CreateDefaultConfig(string configPath)
    {
        var dir = Path.GetDirectoryName(configPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var defaultConfig = new SoundPackConfig
        {
            PrecacheFiles = [DefaultSoundEventFile],
            Packs =
            [
                new SoundPack
                {
                    Id = "default",
                    Name = "Default",
                    Permission = null,
                    Sounds = new Dictionary<string, string>
                    {
                        ["firstblood"]   = "kills.firstblood",
                        ["headshot"]     = "effects.hitmark.headshot",
                        ["doublekill"]   = "crafting.killstreaks.doublekill",
                        ["killingspree"] = "crafting.killstreaks.3_killingspree",
                        ["dominating"]   = "crafting.killstreaks.4_dominating",
                        ["holyshit"]     = "crafting.killstreaks.5_holyshit",
                        ["wickedsick"]   = "crafting.killstreaks.6_wickedsick",
                        ["monsterkill"]  = "crafting.killstreaks.7_monsterkill",
                        ["unstoppable"]  = "crafting.killstreaks.8_unstoppable",
                        ["godlike"]      = "crafting.killstreaks.9_godlike",
                        ["triplekill"]   = "crafting.killstreaks.triplekill",
                        ["quadrakill"]   = "crafting.killstreaks.quadrakill",
                        ["pentakill"]    = "crafting.killstreaks.pentakill",
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
    }
}
