namespace QuakeSounds.Models;

internal static class DefaultTiers
{
    public static TiersConfig Build() => new()
    {
        Killstreaks =
        [
            new() { Count = 4,  SoundKey = "dominating",      LocaleKey = "quakesounds.streak.dominating",      Color = "#FF8C00" },
            new() { Count = 6,  SoundKey = "rampage",         LocaleKey = "quakesounds.streak.rampage",         Color = "#FF8C00" },
            new() { Count = 8,  SoundKey = "killingspree",    LocaleKey = "quakesounds.streak.killingspree",    Color = "#FF8C00" },
            new() { Count = 10, SoundKey = "monsterkill",     LocaleKey = "quakesounds.streak.monsterkill",     Color = "#FF8C00" },
            new() { Count = 14, SoundKey = "unstoppable",     LocaleKey = "quakesounds.streak.unstoppable",     Color = "#FF8C00" },
            new() { Count = 16, SoundKey = "ultrakill",       LocaleKey = "quakesounds.streak.ultrakill",       Color = "#FF8C00" },
            new() { Count = 18, SoundKey = "godlike",         LocaleKey = "quakesounds.streak.godlike",         Color = "#FF8C00" },
            new() { Count = 20, SoundKey = "wickedsick",      LocaleKey = "quakesounds.streak.wickedsick",      Color = "#FF8C00" },
            new() { Count = 22, SoundKey = "impressive",      LocaleKey = "quakesounds.streak.impressive",      Color = "#FF8C00" },
            new() { Count = 24, SoundKey = "ludicrouskill",   LocaleKey = "quakesounds.streak.ludicrouskill",   Color = "#FF8C00" },
            new() { Count = 26, SoundKey = "holyshit",        LocaleKey = "quakesounds.streak.holyshit",        Color = "#FF8C00" },
            new() { Count = 30, SoundKey = "massacre",        LocaleKey = "quakesounds.streak.massacre",        Color = "#FF8C00" },
            new() { Count = 35, SoundKey = "maniac",          LocaleKey = "quakesounds.streak.maniac",          Color = "#FF8C00" },
            new() { Count = 40, SoundKey = "killingmachine",  LocaleKey = "quakesounds.streak.killingmachine",  Color = "#FF8C00" },
            new() { Count = 45, SoundKey = "ownage",          LocaleKey = "quakesounds.streak.ownage",          Color = "#FF8C00" },
            new() { Count = 50, SoundKey = "unreal",          LocaleKey = "quakesounds.streak.unreal",          Color = "#FF8C00" },
            new() { Count = 60, SoundKey = "flawlessvictory", LocaleKey = "quakesounds.streak.flawlessvictory", Color = "#FF8C00" },
        ],
        Combos =
        [
            new() { Count = 2, SoundKey = "doublekill", LocaleKey = "quakesounds.streak.doublekill", Color = "#FFA500" },
            new() { Count = 3, SoundKey = "triplekill", LocaleKey = "quakesounds.streak.triplekill", Color = "#FFA500" },
            new() { Count = 4, SoundKey = "multikill",  LocaleKey = "quakesounds.streak.multikill",  Color = "#FFA500" },
            new() { Count = 5, SoundKey = "megakill",   LocaleKey = "quakesounds.streak.megakill",   Color = "#FFA500" },
            new() { Count = 6, SoundKey = "hexakill",   LocaleKey = "quakesounds.streak.hexakill",   Color = "#FFA500" },
            new() { Count = 7, SoundKey = "comboking",  LocaleKey = "quakesounds.streak.comboking",  Color = "#FFA500" },
        ],
        Headshots =
        [
            new() { Count = 3,  SoundKey = "headhunter",  LocaleKey = "quakesounds.streak.headhunter",  Color = "#FF4500" },
            new() { Count = 5,  SoundKey = "eagleeye",    LocaleKey = "quakesounds.streak.eagleeye",    Color = "#FF4500" },
            new() { Count = 7,  SoundKey = "bullseye",    LocaleKey = "quakesounds.streak.bullseye",    Color = "#FF4500" },
            new() { Count = 9,  SoundKey = "assassin",    LocaleKey = "quakesounds.streak.assassin",    Color = "#FF4500" },
            new() { Count = 11, SoundKey = "outstanding", LocaleKey = "quakesounds.streak.outstanding", Color = "#FF4500" },
        ],
        Special = new()
        {
            FirstBlood   = new() { SoundKey = "firstblood",  LocaleKey = "quakesounds.streak.firstblood",  Color = "#FF0000" },
            KnifeKill    = new() { SoundKey = "humiliation", LocaleKey = "quakesounds.streak.humiliation", Color = "#A020F0" },
            GrenadeKill  = new() { SoundKey = "excellent",   LocaleKey = "quakesounds.streak.excellent",   Color = "#00FFFF" },
            Suicide      = new() { SoundKey = "pancake"     },
            TeamKill     = new() { SoundKey = "teamkiller"  },
            HeadshotDing = new() { SoundKey = "headshot"    },
            RoundStart   = new() { SoundKey = "prepare"     },
        },
    };
}
