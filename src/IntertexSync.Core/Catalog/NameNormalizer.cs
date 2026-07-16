using System.Text.RegularExpressions;

namespace IntertexSync.Core.Catalog;

/// <summary>
/// Нормализация названий товаров при выгрузке 1С→KeyCRM (DEC-013).
/// Чистит косметические опечатки форматирования (docs/07_NAME_QUALITY.csv):
/// двойные точки, кратные пробелы, пробелы по краям и внутри скобок,
/// пробел перед знаком препинания. Применяется ТОЛЬКО к тому, что уходит в KeyCRM;
/// исходные данные 1С не меняются. Функция чистая и идемпотентная.
///
/// Умышленно НЕ трогает: одиночные точки в артикулах (AM52231.003), суффикс `_(1)`,
/// орфографию/язык (напр. «Глітер»/«Гліттер» — это отдельное решение о каноничном
/// написании, а не механическая правка).
/// </summary>
public static partial class NameNormalizer
{
    [GeneratedRegex(@"\.{2,}")] private static partial Regex MultiDot();
    [GeneratedRegex(@"\s+")] private static partial Regex MultiSpace();
    [GeneratedRegex(@"\s+([.,;:)\]])")] private static partial Regex SpaceBeforePunct();
    [GeneratedRegex(@"([(\[])\s+")] private static partial Regex SpaceAfterOpen();

    public static string Normalize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return name ?? string.Empty;

        var s = name;
        s = MultiDot().Replace(s, ".");        // "RJ11110..002.11" -> "RJ11110.002.11"
        s = MultiSpace().Replace(s, " ");      // кратные пробелы/табы -> один пробел
        s = SpaceBeforePunct().Replace(s, "$1"); // "3D . AM" -> "3D. AM"; "x ," -> "x,"
        s = SpaceAfterOpen().Replace(s, "$1");   // "( 3m)" -> "(3m)"
        s = s.Trim();                            // края
        return s;
    }

    /// <summary>true, если название изменится при нормализации (для отчётов/логов).</summary>
    public static bool NeedsNormalization(string? name) =>
        name is not null && !string.Equals(name, Normalize(name), StringComparison.Ordinal);
}
