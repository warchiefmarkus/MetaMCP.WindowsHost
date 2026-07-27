using System.Text.RegularExpressions;

namespace MetaMCP.Host;

internal sealed record ResolvedSshEndpoint(
    string HostName,
    string User,
    int Port,
    string? IdentityFile);

internal static class OpenSshConfig
{
    public static ResolvedSshEndpoint Resolve(ReverseSshSettings settings)
    {
        string? hostName = settings.HostName;
        string? user = settings.User;
        int? port = settings.Port > 0 ? settings.Port : null;
        string? identityFile = settings.PrivateKeyPath;

        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh",
            "config");

        if (File.Exists(configPath))
        {
            foreach (var block in Parse(configPath))
            {
                if (!BlockMatches(settings.Host, block.Patterns))
                {
                    continue;
                }

                hostName ??= block.Values.GetValueOrDefault("hostname");
                user ??= block.Values.GetValueOrDefault("user");
                identityFile ??= block.Values.GetValueOrDefault("identityfile");
                if (port is null &&
                    int.TryParse(block.Values.GetValueOrDefault("port"), out var parsedPort))
                {
                    port = parsedPort;
                }
            }
        }

        hostName ??= settings.Host;
        user ??= Environment.UserName;
        port ??= 22;
        identityFile = ExpandPath(identityFile, hostName, user);

        if (identityFile is null)
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            identityFile = new[] { "id_ed25519", "id_rsa", "id_ecdsa" }
                .Select(name => Path.Combine(profile, ".ssh", name))
                .FirstOrDefault(File.Exists);
        }

        return new ResolvedSshEndpoint(hostName, user, port.Value, identityFile);
    }

    public static void PersistResolvedValues(ReverseSshSettings settings)
    {
        if (!settings.Enabled)
        {
            return;
        }

        var resolved = Resolve(settings);
        settings.HostName = resolved.HostName;
        settings.User = resolved.User;
        settings.Port = resolved.Port;
        settings.PrivateKeyPath = resolved.IdentityFile;
    }

    private static IReadOnlyList<SshConfigBlock> Parse(string path)
    {
        var blocks = new List<SshConfigBlock>();
        var current = new SshConfigBlock(["*"]);
        blocks.Add(current);

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOfAny([' ', '\t', '=']);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim().ToLowerInvariant();
            var value = line[(separator + 1)..].Trim();
            if (value.StartsWith('='))
            {
                value = value[1..].Trim();
            }
            value = value.Trim('"');

            if (key == "host")
            {
                current = new SshConfigBlock(
                    value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
                blocks.Add(current);
                continue;
            }

            if (!current.Values.ContainsKey(key))
            {
                current.Values[key] = value;
            }
        }

        return blocks;
    }

    private static bool BlockMatches(string host, IReadOnlyCollection<string> patterns)
    {
        var positivePatterns = patterns.Where(pattern => !pattern.StartsWith('!')).ToArray();
        var negativePatterns = patterns
            .Where(pattern => pattern.StartsWith('!'))
            .Select(pattern => pattern[1..])
            .ToArray();

        return positivePatterns.Any(pattern => WildcardMatch(host, pattern)) &&
               !negativePatterns.Any(pattern => WildcardMatch(host, pattern));
    }

    private static bool WildcardMatch(string host, string pattern)
    {
        var expression = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(host, expression, RegexOptions.IgnoreCase);
    }

    private static string StripComment(string line)
    {
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
            {
                quoted = !quoted;
            }
            else if (line[index] == '#' && !quoted)
            {
                return line[..index];
            }
        }

        return line;
    }

    private static string? ExpandPath(string? value, string host, string user)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'))
            .Replace("%h", host, StringComparison.OrdinalIgnoreCase)
            .Replace("%r", user, StringComparison.OrdinalIgnoreCase);

        if (value.StartsWith("~/") || value.StartsWith("~\\"))
        {
            value = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                value[2..]);
        }

        return Path.GetFullPath(value);
    }

    private sealed class SshConfigBlock(IEnumerable<string> patterns)
    {
        public string[] Patterns { get; } = patterns.ToArray();
        public Dictionary<string, string> Values { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}