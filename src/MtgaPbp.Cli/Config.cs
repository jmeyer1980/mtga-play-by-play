using System.Text.Json;

namespace MtgaPbp.Cli;

public sealed class Config
{
    public string[] LogPaths { get; set; } = [];
    public string? CardDbPath { get; set; }
    public string ArchiveDir { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public string? LocalPlayerUserId { get; set; }

    /// <summary>
    /// Open index.html in the default browser after a build. Off by default; set it
    /// when you launch the tool by double-clicking, where the console window closes
    /// before the output path can be read.
    /// </summary>
    public bool OpenAfterBuild { get; set; }

    /// <summary>
    /// Keep at most this many matches, dropping the oldest as new ones arrive.
    /// Favourites never count against it and are never dropped. Zero means no limit,
    /// which is the default — deleting someone's match history on the first run after
    /// an upgrade would be a poor surprise.
    /// </summary>
    public int MaxArchivedMatches { get; set; }

    public static Config Default()
    {
        var low = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Wizards Of The Coast", "MTGA");
        var home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MTGA_PlayByPlay");

        return new Config
        {
            LogPaths = [Path.Combine(low, "Player.log"), Path.Combine(low, "Player-prev.log")],
            ArchiveDir = Path.Combine(home, "archive"),
            OutputDir = Path.Combine(home, "out"),
        };
    }

    /// <summary>The config the user owns. Never shipped, so an upgrade cannot touch it.</summary>
    public const string UserFile = "mtga-pbp.json";

    /// <summary>
    /// The config the release ships. Overwritten by every upgrade on purpose.
    /// </summary>
    /// <remarks>
    /// It exists so that a fresh install still opens its report when the exe is
    /// double-clicked — the console closes before the output path can be read, so
    /// without <see cref="OpenAfterBuild"/> the first run looks like it did nothing.
    /// That setting used to ship in <see cref="UserFile"/> itself, which meant
    /// unzipping an upgrade over the folder replaced whatever the user had put there.
    /// Nothing was deleted, but a custom <see cref="ArchiveDir"/> was forgotten, the
    /// next run built a fresh archive at the default location, and the report came up
    /// with no matches in it (#134).
    /// </remarks>
    public const string ShippedFile = "mtga-pbp.defaults.json";

    /// <summary>
    /// The effective config: defaults, then what the release shipped, then what the
    /// user wrote. Later layers win key by key, so the shipped file can carry a
    /// setting the user has never heard of without taking away one they chose.
    /// </summary>
    public static Config Load(string exeDir)
    {
        var cfg = Default();
        Apply(cfg, Path.Combine(exeDir, ShippedFile));
        Apply(cfg, Path.Combine(exeDir, UserFile));
        return cfg;
    }

    /// <summary>
    /// One layer of config over another, applying only the keys the file actually
    /// carried.
    /// </summary>
    /// <remarks>
    /// Deserialized into <see cref="Layer"/> rather than into <see cref="Config"/>
    /// so that "the key was absent" and "the key was set to the type's default" stay
    /// tellable apart. With one file they were the same thing; with two they are not.
    /// <see cref="OpenAfterBuild"/> is the case that would have broken: a bool has no
    /// unset state, so a user file that never mentions it deserializes to false, and
    /// applying that would switch off a setting the shipped layer had turned on.
    /// </remarks>
    private static void Apply(Config cfg, string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            if (JsonSerializer.Deserialize<Layer>(File.ReadAllText(path)) is not { } loaded) return;

            if (loaded.LogPaths is { Length: > 0 }) cfg.LogPaths = loaded.LogPaths;
            if (!string.IsNullOrWhiteSpace(loaded.CardDbPath)) cfg.CardDbPath = loaded.CardDbPath;
            if (!string.IsNullOrWhiteSpace(loaded.ArchiveDir)) cfg.ArchiveDir = loaded.ArchiveDir;
            if (!string.IsNullOrWhiteSpace(loaded.OutputDir)) cfg.OutputDir = loaded.OutputDir;
            if (!string.IsNullOrWhiteSpace(loaded.LocalPlayerUserId))
                cfg.LocalPlayerUserId = loaded.LocalPlayerUserId;
            if (loaded.OpenAfterBuild is { } open) cfg.OpenAfterBuild = open;
            if (loaded.MaxArchivedMatches > 0) cfg.MaxArchivedMatches = loaded.MaxArchivedMatches.Value;
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"Ignoring malformed {path}; using defaults.");
        }
    }

    /// <summary>
    /// A config file as written, where every absent key stays absent. Separate from
    /// <see cref="Config"/>, which has no way to say "not stated".
    /// </summary>
    private sealed class Layer
    {
        public string[]? LogPaths { get; set; }
        public string? CardDbPath { get; set; }
        public string? ArchiveDir { get; set; }
        public string? OutputDir { get; set; }
        public string? LocalPlayerUserId { get; set; }
        public bool? OpenAfterBuild { get; set; }
        public int? MaxArchivedMatches { get; set; }
    }
}
