using System.Text.RegularExpressions;
using VdsServiceUpdater.Images;

namespace VdsServiceUpdater.Compose;

/// <summary>
/// Чистая функция "YAML-текст → YAML-текст": меняет только тег в строке image: у подходящих сервисов.
/// Всё остальное (комментарии, кавычки, отступы, переводы строк) остаётся байт-в-байт.
/// Если строку нельзя безопасно изменить или проверка после правки не прошла, бросает ComposeFileException.
/// </summary>
public static class ComposeImageEditor
{
    // [отступ]image: [значение в кавычках или без] [# комментарий]
    private static readonly Regex ImageLine = new(
        @"^(?<prefix>[ ]*image[ \t]*:[ \t]+)(?<value>""[^""\\]*""|'[^']*'|[^\s#""'][^\s#]*)(?<suffix>[ \t]*(?:#.*)?)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static EditResult Apply(string yaml, ImageReference target, IReadOnlyCollection<string> protectedPatterns)
    {
        if (target.Tag is null) throw new ArgumentException("У целевого образа должен быть тег.", nameof(target));

        var before = ComposeFileScanner.Scan(yaml);
        var lines = Regex.Split(yaml, "(?<=\n)").ToList(); // строки вместе с переводом строки
        var changes = new List<ServiceChange>();
        var expected = new Dictionary<string, string>();   // сервис → ожидаемое значение image после правки
        var edited = new Dictionary<int, string>();        // строка → новое значение (общая строка у нескольких сервисов)

        foreach (var s in before)
        {
            if (s.Reference is null || s.Reference.Repository != target.Repository) continue;

            if (protectedPatterns.Any(p => GlobMatcher.IsMatch(p, s.Service)))
            { changes.Add(new(s.Service, ServiceOutcome.SkippedProtected, s.RawValue, s.RawValue, s.Line)); continue; }
            if (s.UsesVariable)
            { changes.Add(new(s.Service, ServiceOutcome.SkippedVariable, s.RawValue, s.RawValue, s.Line)); continue; }
            if (s.Reference.Digest is not null)
            { changes.Add(new(s.Service, ServiceOutcome.SkippedPinned, s.RawValue, s.RawValue, s.Line)); continue; }
            if (s.Reference.Tag == target.Tag)
            { changes.Add(new(s.Service, ServiceOutcome.Unchanged, s.RawValue, s.RawValue, s.Line)); continue; }

            // Пишем репозиторий в той же форме, что в файле ("nginx" остаётся "nginx"), меняем только тег.
            var repoText = s.Reference.Tag is null ? s.RawValue : s.RawValue[..^(s.Reference.Tag.Length + 1)];
            var newValue = $"{repoText}:{target.Tag}";

            if (!edited.ContainsKey(s.Line))
            {
                ReplaceOnLine(lines, s, newValue);
                edited[s.Line] = newValue;
            }
            expected[s.Service] = newValue;
            changes.Add(new(s.Service, ServiceOutcome.Updated, s.RawValue, newValue, s.Line));
        }

        if (expected.Count == 0) return new EditResult(yaml, changes);

        var content = string.Concat(lines);
        Verify(content, before, expected);
        return new EditResult(content, changes);
    }

    private static void ReplaceOnLine(List<string> lines, ServiceImage s, string newValue)
    {
        var index = s.Line - 1;
        var unsupported = new ComposeFileException("unsupported_format",
            $"Сервис '{s.Service}': строка image: (строка {s.Line}) имеет формат, который нельзя безопасно изменить " +
            "(якорь/алиас, flow-стиль, многострочное значение). Отредактируйте файл вручную.");

        if (index < 0 || index >= lines.Count) throw unsupported;

        var line = lines[index];
        var content = line.TrimEnd('\r', '\n');
        var eol = line[content.Length..];

        var m = ImageLine.Match(content);
        if (!m.Success) throw unsupported;

        var token = m.Groups["value"].Value;
        var quote = token[0] is '"' or '\'' ? token[0].ToString() : "";
        var inner = quote.Length > 0 ? token[1..^1] : token;
        if (inner != s.RawValue) throw unsupported;

        lines[index] = m.Groups["prefix"].Value + quote + newValue + quote + m.Groups["suffix"].Value + eol;
    }

    /// <summary>Повторный разбор: изменились ровно ожидаемые сервисы и ровно на ожидаемые значения.</summary>
    private static void Verify(string content, IReadOnlyList<ServiceImage> before, Dictionary<string, string> expected)
    {
        IReadOnlyList<ServiceImage> after;
        try { after = ComposeFileScanner.Scan(content); }
        catch (ComposeFileException ex) { throw Fail($"результат не разбирается как compose-файл ({ex.Message})"); }

        if (after.Count != before.Count) throw Fail("изменилось число сервисов с image:");
        var actual = after.ToDictionary(x => x.Service, x => x.RawValue);

        foreach (var b in before)
        {
            var want = expected.TryGetValue(b.Service, out var e) ? e : b.RawValue;
            if (!actual.TryGetValue(b.Service, out var got) || got != want)
                throw Fail($"сервис '{b.Service}': ожидалось '{want}', получено '{got}' (возможно, общий якорь YAML)");
        }
    }

    private static ComposeFileException Fail(string reason) =>
        new("verification_failed", $"Проверка после правки не прошла: {reason}. Файл не изменён.");
}
