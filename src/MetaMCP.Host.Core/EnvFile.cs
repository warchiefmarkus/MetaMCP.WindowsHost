using System.Text.RegularExpressions;

namespace MetaMCP.Host;

/// <summary>
/// Loads .env files for both the runtime host and the release packager.
/// References are resolved from this file first, then from the process environment.
/// </summary>
internal static class EnvFile
{
    private static readonly Regex ReferencePattern = new(
        @"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static Dictionary<string, string> Load(string path)
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return raw;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].TrimStart();
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            var value = RemoveInlineComment(line[(separator + 1)..].Trim());
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            raw[key] = value;
        }

        var expanded = new Dictionary<string, string>(raw.Comparer);
        var inProgress = new HashSet<string>(raw.Comparer);

        string Resolve(string key)
        {
            if (expanded.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (!inProgress.Add(key))
            {
                throw new InvalidDataException(
                    "Circular .env variable reference involving: " + key);
            }

            try
            {
                var value = ReferencePattern.Replace(raw[key], match =>
                {
                    var referencedKey = match.Groups["name"].Value;
                    if (raw.ContainsKey(referencedKey))
                    {
                        return Resolve(referencedKey);
                    }

                    var inheritedValue = Environment.GetEnvironmentVariable(referencedKey);
                    if (inheritedValue is not null)
                    {
                        return inheritedValue;
                    }

                    throw new InvalidDataException(
                        "Undefined .env variable " + match.Value + " referenced by " + key);
                });

                expanded[key] = value;
                return value;
            }
            finally
            {
                inProgress.Remove(key);
            }
        }

        foreach (var key in raw.Keys)
        {
            Resolve(key);
        }

        return expanded;
    }

    private static string RemoveInlineComment(string value)
    {
        var quotedBy = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (quotedBy != '\0')
            {
                if (current == '\\' && i + 1 < value.Length)
                {
                    i++;
                    continue;
                }

                if (current == quotedBy)
                {
                    quotedBy = '\0';
                }

                continue;
            }

            if (current == '\'' || current == '"')
            {
                quotedBy = current;
            }
            else if (current == '#' &&
                     (i == 0 || char.IsWhiteSpace(value[i - 1])))
            {
                return value[..i].TrimEnd();
            }
        }

        return value;
    }
}
