using System.Globalization;

namespace HoverTranslate.Core.Storage;

/// <summary>Размер данных пользователя на диске.</summary>
public static class DiskUsage
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Файл базы вместе с журналом WAL и файлом общей памяти; несуществующие файлы считаются нулём.</summary>
    public static long DatabaseSize(string dbPath)
    {
        long total = 0;
        foreach (string path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) total += info.Length;
            }
            catch (IOException)
            {
                // файл исчез или занят — это только справочная цифра
            }
        }
        return total;
    }

    /// <summary>«512 Б», «3,4 КБ», «1,2 МБ».</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb.ToString("0.#", Russian) + " КБ";
        return (kb / 1024.0).ToString("0.#", Russian) + " МБ";
    }
}
