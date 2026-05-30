using System.Text.Json.Serialization;

namespace QuakeSounds.Models;

internal sealed class TierEntry
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("soundKey")]
    public string SoundKey { get; set; } = string.Empty;

    [JsonPropertyName("localeKey")]
    public string? LocaleKey { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }
}

internal sealed class SpecialEntry
{
    [JsonPropertyName("soundKey")]
    public string SoundKey { get; set; } = string.Empty;

    [JsonPropertyName("localeKey")]
    public string? LocaleKey { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }
}

internal sealed class SpecialTiers
{
    [JsonPropertyName("first_blood")]   public SpecialEntry? FirstBlood   { get; set; }
    [JsonPropertyName("knife_kill")]    public SpecialEntry? KnifeKill    { get; set; }
    [JsonPropertyName("grenade_kill")]  public SpecialEntry? GrenadeKill  { get; set; }
    [JsonPropertyName("suicide")]       public SpecialEntry? Suicide      { get; set; }
    [JsonPropertyName("team_kill")]     public SpecialEntry? TeamKill     { get; set; }
    [JsonPropertyName("headshot_ding")] public SpecialEntry? HeadshotDing { get; set; }
    [JsonPropertyName("round_start")]   public SpecialEntry? RoundStart   { get; set; }
}

internal sealed class TiersConfig
{
    [JsonPropertyName("killstreaks")] public List<TierEntry> Killstreaks { get; set; } = [];
    [JsonPropertyName("combos")]      public List<TierEntry> Combos      { get; set; } = [];
    [JsonPropertyName("headshots")]   public List<TierEntry> Headshots   { get; set; } = [];
    [JsonPropertyName("special")]     public SpecialTiers?  Special     { get; set; }
}
