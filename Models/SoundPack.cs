using System.Text.Json.Serialization;

namespace QuakeSounds.Models;

internal sealed class SoundPack
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("permission")]
    public string? Permission { get; set; }

    [JsonPropertyName("sounds")]
    public Dictionary<string, string> Sounds { get; set; } = new();

    public string? GetSound(string key)
    {
        return Sounds.TryGetValue(key, out var sound) ? sound : null;
    }
}

internal sealed class SoundPackConfig
{
    [JsonPropertyName("packs")]
    public List<SoundPack> Packs { get; set; } = [];

    /// <summary>
    /// Soundevent (.vsndevts) files to precache so the configured soundevents are playable.
    /// Without this the engine never registers the events and <c>EmitSoundClient</c> is silent.
    /// </summary>
    [JsonPropertyName("precacheFiles")]
    public List<string> PrecacheFiles { get; set; } = [];
}
