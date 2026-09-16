namespace LimbusTranslator.Infrastructure.Services;

public sealed class GameLocateResult
{
    public required string GameRoot { get; init; }
    public required string NewEnglishDir { get; init; }
    public required string ChineseDir { get; init; }
    public bool IsAutoDetected { get; init; }
}

public static class GameDirectoryLocator
{
    public const string GameExeName = "LimbusCompany.exe";
    private const string EnglishRel = "LimbusCompany_Data\\Assets\\Resources_moved\\Localize\\en";
    private const string ChineseRel = "LimbusCompany_Data\\Lang\\LLC_zh-CN";

    public static GameLocateResult? AutoLocate()
    {
        var fromApp = FindByWalkingUp(AppContext.BaseDirectory);
        if (fromApp is not null) return fromApp;
        var fromCwd = FindByWalkingUp(Directory.GetCurrentDirectory());
        if (fromCwd is not null) return fromCwd;
        var fromSteam = FindBySteamRegistry();
        if (fromSteam is not null) return fromSteam;
        return FindByCommonPaths();
    }

    private static GameLocateResult? FindByWalkingUp(string start)
    {
        try
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (IsGameRoot(dir.FullName))
                {
                    return BuildResult(dir.FullName, auto: true);
                }
                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }

    private static GameLocateResult? FindBySteamRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\\Valve\\Steam");
            var steamPath = key?.GetValue("SteamPath") as string;
            if (string.IsNullOrWhiteSpace(steamPath)) return null;
            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (var lib in ParseLibraryFolders(vdf))
                {
                    var candidate = Path.Combine(lib, "steamapps", "common", "Limbus Company");
                    if (IsGameRoot(candidate)) return BuildResult(candidate, auto: true);
                }
            }
            var defaultLib = Path.Combine(steamPath, "steamapps", "common", "Limbus Company");
            if (IsGameRoot(defaultLib)) return BuildResult(defaultLib, auto: true);
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> ParseLibraryFolders(string vdfPath)
    {
        var libs = new List<string>();
        try
        {
            foreach (var line in File.ReadLines(vdfPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('"') && trimmed.Contains("\"\\\""))
                {
                    var parts = trimmed.Split('"');
                    if (parts.Length >= 5 && !string.IsNullOrWhiteSpace(parts[3]))
                    {
                        libs.Add(parts[3]);
                    }
                }
            }
        }
        catch { }
        return libs;
    }

    private static GameLocateResult? FindByCommonPaths()
    {
        var candidates = new[]
        {
            @"C:\\Program Files (x86)\\Steam\\steamapps\\common\\Limbus Company",
            @"C:\\Program Files\\Steam\\steamapps\\common\\Limbus Company",
            @"D:\\SteamLibrary\\steamapps\\common\\Limbus Company",
            @"D:\\Steam\\steamapps\\common\\Limbus Company",
            @"E:\\SteamLibrary\\steamapps\\common\\Limbus Company",
            @"E:\\Steam\\steamapps\\common\\Limbus Company",
        };
        foreach (var candidate in candidates)
        {
            if (IsGameRoot(candidate)) return BuildResult(candidate, auto: true);
        }
        return null;
    }

    public static GameLocateResult BuildResult(string gameRoot, bool auto)
    {
        return new GameLocateResult
        {
            GameRoot = gameRoot,
            NewEnglishDir = Path.Combine(gameRoot, EnglishRel),
            ChineseDir = Path.Combine(gameRoot, ChineseRel),
            IsAutoDetected = auto,
        };
    }

    public static bool IsGameRoot(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                   && (File.Exists(Path.Combine(dir, GameExeName))
                       || Directory.Exists(Path.Combine(dir, "LimbusCompany_Data")));
        }
        catch { return false; }
    }

    public static bool IsEnglishDirValid(string dir)
        => Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.json").Any();

    public static bool IsChineseDirValid(string dir)
        => Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.json").Any();
}