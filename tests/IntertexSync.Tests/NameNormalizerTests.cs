using IntertexSync.Core.Catalog;
using Xunit;

namespace IntertexSync.Tests;

/// <summary>Тесты нормализации названий на РЕАЛЬНЫХ примерах из каталога (docs/07_NAME_QUALITY.csv).</summary>
public sealed class NameNormalizerTests
{
    [Theory]
    // двойная точка (подтверждённая опечатка владельца)
    [InlineData("Шантилье RJ11110..002.11_(1)", "Шантилье RJ11110.002.11_(1)")]
    // двойные пробелы
    [InlineData("Гипюр  EL1905-S_(1)", "Гипюр EL1905-S_(1)")]
    [InlineData("Гліттер  2496", "Гліттер 2496")]
    // пробел по краям
    [InlineData(" Хрусталики Randele", "Хрусталики Randele")]
    [InlineData("Євросітка ANGEL з перлинками ", "Євросітка ANGEL з перлинками")]
    // пробел перед знаком
    [InlineData("Аплікація 3D . AM26471.701.01", "Аплікація 3D. AM26471.701.01")]
    // пробелы внутри скобок + двойные пробелы вместе
    [InlineData("Воск Karolin  G00022  ( 3m)", "Воск Karolin G00022 (3m)")]
    // комбинация: двойной пробел внутри
    [InlineData("Мереживо шантильї 8962-3   прозоре по 5", "Мереживо шантильї 8962-3 прозоре по 5")]
    public void Normalize_FixesFormattingTypos(string input, string expected)
        => Assert.Equal(expected, NameNormalizer.Normalize(input));

    [Theory]
    // легитимные названия НЕ меняются
    [InlineData("Шантилье AM52231.003_(1)")]        // одиночные точки в артикуле
    [InlineData("Гіпюр Y0001AL_(YL0000B)")]         // скобки и подчёркивания
    [InlineData("Кружево AC11116.001.01_(1)")]
    [InlineData("Гіпюр 00000")]                     // «заглушечный» код — норма для владельца
    public void Normalize_LeavesCleanNamesUnchanged(string input)
        => Assert.Equal(input, NameNormalizer.Normalize(input));

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var messy = "  Воск  Karolin  G00022 . ( 3m)  ";
        var once = NameNormalizer.Normalize(messy);
        Assert.Equal(once, NameNormalizer.Normalize(once)); // повторная нормализация ничего не меняет
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Normalize_HandlesEmpty(string? input)
        => Assert.Equal(string.Empty, NameNormalizer.Normalize(input));

    [Fact]
    public void NeedsNormalization_DetectsCorrectly()
    {
        Assert.True(NameNormalizer.NeedsNormalization("Гипюр  2119_(1)"));
        Assert.False(NameNormalizer.NeedsNormalization("Гипюр 2119_(1)"));
    }

    /// <summary>Все 24 реально «грязных» названия из каталога KeyCRM (2026-07-16)
    /// после нормализации не содержат ни одной форматной аномалии.</summary>
    [Fact]
    public void Normalize_AllRealKeyCrmMessyNames_BecomeClean()
    {
        string[] real =
        {
            "Аплікаціі 3D . AM26471.701.01", "Воск Karolin  G00022  ( 3m)",
            "Гипюр  2119_(1)", "Гипюр  2235_ори_(1)", "Гипюр  EL1901-S_(1)",
            "Гипюр  EL1902-S_(1)", "Гипюр  EL1905-S_(1)", "Гипюр  EL1906-S_(1)",
            "Гипюр  EL1907-S_(1)", "Гипюр  EL1908-S_(1)", "Гипюр  EL1909-S_(1)",
            "Гипюр  EL2201_(1)", "Гипюр  EL2202_(1)", "Гипюр  GX1784_(1)",
            "Гипюр  H502-4_(1)", "Гипюр  HM1383_(1)", "Глітер  2698", "Глітер  2734",
            "Гліттер  2496", "Гліттер  2611", "Гліттер  2695", "Гліттер  2757",
            "Гліттер  2760", "Шантилье RJ11110..002.11_(1)",
        };
        foreach (var name in real)
        {
            var n = NameNormalizer.Normalize(name);
            Assert.DoesNotContain("  ", n);      // нет двойных пробелов
            Assert.DoesNotContain("..", n);      // нет двойных точек
            Assert.DoesNotContain(" .", n);      // нет пробела перед точкой
            Assert.DoesNotContain(" ,", n);      // нет пробела перед запятой
            Assert.DoesNotContain("( ", n);      // нет пробела после скобки
            Assert.Equal(n, n.Trim());           // нет краевых пробелов
            Assert.True(NameNormalizer.Normalize(n) == n); // идемпотентно
        }
    }
}
