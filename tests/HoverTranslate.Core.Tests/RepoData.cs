namespace HoverTranslate.Core.Tests;

/// <summary>Данные из tools/DataBuilder (data/ в корне репозитория, в git не хранится).</summary>
internal static class RepoData
{
    public static string? Root { get; } = FindRoot();

    /// <summary>data/dict.db или null, если словарь не собран.</summary>
    public static string? Dictionary => Existing(Root is null ? null : Path.Combine(Root, "data", "dict.db"));

    /// <summary>data/models/opus-mt-en-ru или null, если модель не экспортирована.</summary>
    public static string? Model => Root is null ? null
        : Directory.Exists(Path.Combine(Root, "data", "models", "opus-mt-en-ru")) ? Path.Combine(Root, "data", "models", "opus-mt-en-ru") : null;

    private static string? Existing(string? path) => path is not null && File.Exists(path) ? path : null;

    private static string? FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HoverTranslate.sln"))) return dir.FullName;
        }
        return null;
    }
}
