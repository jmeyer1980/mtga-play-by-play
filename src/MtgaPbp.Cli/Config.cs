using System.Text.Json;

namespace MtgaPbp.Cli;

public sealed class Config
{
    public string[] LogPaths { get; set; } = [];
    public string? CardDbPath { get; set; }
    public string ArchiveDir { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public string? LocalPlayerUserId { get; set; }

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

    public static Config Load(string exeDir)
    {
        var path = Path.Combine(exeDir, "mtga-pbp.json");
        var cfg = Default();
        if (!File.Exists(path)) return cfg;

        try
        {
            var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(path));
            if (loaded is null) return cfg;
            if (loaded.LogPaths.Length > 0) cfg.LogPaths = loaded.LogPaths;
            if (!string.IsNullOrWhiteSpace(loaded.CardDbPath)) cfg.CardDbPath = loaded.CardDbPath;
            if (!string.IsNullOrWhiteSpace(loaded.ArchiveDir)) cfg.ArchiveDir = loaded.ArchiveDir;
            if (!string.IsNullOrWhiteSpace(loaded.OutputDir)) cfg.OutputDir = loaded.OutputDir;
            if (!string.IsNullOrWhiteSpace(loaded.LocalPlayerUserId))
                cfg.LocalPlayerUserId = loaded.LocalPlayerUserId;
            return cfg;
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"Ignoring malformed {path}; using defaults.");
            return cfg;
        }
    }
}
