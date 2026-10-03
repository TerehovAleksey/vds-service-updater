using YamlDotNet.RepresentationModel;
using VdsServiceUpdater.Images;

namespace VdsServiceUpdater.Compose;

/// <summary>Только чтение: находит сервисы с image: и номера строк. YAML не переписывается.</summary>
public static class ComposeFileScanner
{
    public static IReadOnlyList<ServiceImage> Scan(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (Exception ex)
        {
            throw new ComposeFileException("invalid_yaml", $"Файл не является корректным YAML: {ex.Message}");
        }

        if (stream.Documents.Count == 0)
            throw new ComposeFileException("no_services", "Файл пуст или в нём нет секции services.");
        if (stream.Documents.Count > 1)
            throw new ComposeFileException("unsupported_format", "Ожидается ровно один YAML-документ.");

        if (stream.Documents[0].RootNode is not YamlMappingNode root ||
            !TryGet(root, "services", out var servicesNode) ||
            servicesNode is not YamlMappingNode services)
            throw new ComposeFileException("no_services", "В файле нет секции services.");

        var result = new List<ServiceImage>();
        foreach (var kv in services.Children)
        {
            if (kv.Key is not YamlScalarNode { Value: { } name } || kv.Value is not YamlMappingNode service) continue;

            foreach (var field in service.Children)
            {
                if (field.Key is not YamlScalarNode { Value: "image" } key ||
                    field.Value is not YamlScalarNode { Value: { Length: > 0 } raw }) continue;

                var (reference, usesVariable) = Interpret(raw);
                result.Add(new ServiceImage(name, raw, (int)key.Start.Line, reference, usesVariable));
            }
        }
        return result;
    }

    /// <summary>"app:${TAG}" считается образом app (с переменной в теге); "${IMAGE}" не разбирается.</summary>
    private static (ImageReference? Reference, bool UsesVariable) Interpret(string raw)
    {
        if (raw.Contains('$'))
        {
            var colon = raw.LastIndexOf(':');
            if (colon > raw.LastIndexOf('/'))
            {
                var repo = raw[..colon];
                if (!repo.Contains('$') && ImageReference.TryParse(repo, out var r, out _) && r!.Tag is null && r.Digest is null)
                    return (r, true);
            }
            return (null, true);
        }
        return ImageReference.TryParse(raw, out var parsed, out _) ? (parsed, false) : (null, false);
    }

    private static bool TryGet(YamlMappingNode map, string key, out YamlNode value)
    {
        foreach (var kv in map.Children)
            if (kv.Key is YamlScalarNode { Value: { } k } && k == key)
            {
                value = kv.Value;
                return true;
            }
        value = null!;
        return false;
    }
}
