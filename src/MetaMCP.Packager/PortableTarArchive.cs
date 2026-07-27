using System.Formats.Tar;
using System.IO.Compression;

namespace MetaMCP.Packager;

internal static class PortableTarArchive
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode RegularFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode ExecutableFileMode = DirectoryMode;

    public static void CreateFromDirectory(
        string sourceDirectory,
        string archivePath)
    {
        var source = new DirectoryInfo(sourceDirectory);
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException(source.FullName);
        }

        File.Delete(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using var output = File.Create(archivePath);
        using var gzip = new GZipStream(
            output,
            CompressionLevel.SmallestSize,
            leaveOpen: false);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        WriteDirectory(writer, source, source.Name);
    }

    private static void WriteDirectory(
        TarWriter writer,
        DirectoryInfo directory,
        string entryName)
    {
        WriteEntry(
            writer,
            new PaxTarEntry(TarEntryType.Directory, Normalize(entryName))
            {
                Mode = DirectoryMode,
            });

        foreach (var entry in directory.EnumerateFileSystemInfos()
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var childName = Normalize(Path.Combine(entryName, entry.Name));
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                WriteSymbolicLink(writer, entry, childName);
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                WriteDirectory(writer, childDirectory, childName);
            }
            else if (entry is FileInfo file)
            {
                WriteRegularFile(writer, file, childName);
            }
        }
    }

    private static void WriteSymbolicLink(
        TarWriter writer,
        FileSystemInfo link,
        string entryName)
    {
        var target = link.LinkTarget
            ?? throw new IOException($"Could not read symlink target: {link.FullName}");
        var entry = new PaxTarEntry(
            TarEntryType.SymbolicLink,
            Normalize(entryName))
        {
            LinkName = Normalize(target),
            Mode = link is DirectoryInfo ? DirectoryMode : RegularFileMode,
        };
        WriteEntry(writer, entry);
    }

    private static void WriteRegularFile(
        TarWriter writer,
        FileInfo file,
        string entryName)
    {
        using var data = file.OpenRead();
        var entry = new PaxTarEntry(
            TarEntryType.RegularFile,
            Normalize(entryName))
        {
            DataStream = data,
            Mode = IsExecutable(entryName)
                ? ExecutableFileMode
                : RegularFileMode,
        };
        WriteEntry(writer, entry);
    }

    private static void WriteEntry(TarWriter writer, TarEntry entry)
    {
        entry.ModificationTime = DateTimeOffset.UnixEpoch;
        entry.Uid = 0;
        entry.Gid = 0;
        writer.WriteEntry(entry);
    }

    private static bool IsExecutable(string entryName)
    {
        var normalized = Normalize(entryName);
        return normalized.EndsWith("/metamcp-host", StringComparison.Ordinal) ||
               normalized.EndsWith("/runtime/node/bin/node", StringComparison.Ordinal) ||
               normalized.EndsWith("/runtime/node/bin/npm", StringComparison.Ordinal) ||
               normalized.EndsWith("/runtime/node/bin/npx", StringComparison.Ordinal) ||
               normalized.EndsWith("/deploy/install-systemd.sh", StringComparison.Ordinal) ||
               normalized.Contains("/runtime/node/bin/", StringComparison.Ordinal);
    }

    private static string Normalize(string value) => value.Replace('\\', '/');
}
