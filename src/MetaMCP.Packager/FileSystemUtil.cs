namespace MetaMCP.Packager;

internal static class FileSystemUtil
{
    public static void RecreateDirectory(string path)
    {
        DeleteDirectory(path);
        Directory.CreateDirectory(path);
    }

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        DeleteDirectoryCore(new DirectoryInfo(path));
    }

    public static void CopyDirectory(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (!sourceInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {source}");
        }

        CopyDirectoryCore(sourceInfo, new DirectoryInfo(destination));
    }

    public static void CopyDirectoryPreserveLinks(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (!sourceInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {source}");
        }

        CopyDirectoryPreserveLinksCore(sourceInfo, new DirectoryInfo(destination));
    }

    public static void CopyFile(string source, string destination)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Source file not found.", source);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 8)
            {
                Thread.Sleep(attempt * 150);
            }
            catch (UnauthorizedAccessException) when (attempt < 8)
            {
                Thread.Sleep(attempt * 150);
            }
        }
    }

    public static void RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Required release file was not produced.", path);
        }
    }

    public static long GetDirectorySize(string path)
    {
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(path));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry is FileInfo file)
                {
                    total += file.Length;
                }
                else if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }
        return total;
    }

    public static int MakeReparsePointsPortable(string root)
    {
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var links = EnumerateReparsePoints(root)
            .OfType<DirectoryInfo>()
            .OrderByDescending(link => link.FullName.Length)
            .ToArray();
        var converted = 0;
        foreach (var link in links)
        {
            var target = link.ResolveLinkTarget(returnFinalTarget: false) as DirectoryInfo
                ?? throw new IOException($"Could not resolve directory link: {link.FullName}");
            var targetFull = Path.GetFullPath(target.FullName);

            if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                if (IsWorkspaceSelfLink(link))
                {
                    link.Delete();
                    continue;
                }

                throw new InvalidOperationException(
                    $"Release link points outside the release directory: {link.FullName} -> {targetFull}");
            }

            var parent = link.Parent
                ?? throw new InvalidOperationException($"Link has no parent: {link.FullName}");
            var relativeTarget = Path.GetRelativePath(parent.FullName, targetFull);
            link.Delete();
            Directory.CreateSymbolicLink(link.FullName, relativeTarget);
            converted++;
        }

        return converted;
    }

    public static IReadOnlyList<string> GetBrokenReparsePoints(string root)
    {
        var broken = new List<string>();
        foreach (var info in EnumerateReparsePoints(root))
        {
            try
            {
                if (info.ResolveLinkTarget(returnFinalTarget: true) is null)
                {
                    broken.Add(info.FullName);
                }
            }
            catch
            {
                broken.Add(info.FullName);
            }
        }
        return broken;
    }

    public static IReadOnlyList<string> GetNonPortableReparsePoints(string root)
    {
        var nonPortable = new List<string>();
        foreach (var info in EnumerateReparsePoints(root))
        {
            var target = info.LinkTarget;
            if (string.IsNullOrWhiteSpace(target) || Path.IsPathRooted(target))
            {
                nonPortable.Add(info.FullName);
            }
        }
        return nonPortable;
    }

    private static IReadOnlyList<FileSystemInfo> EnumerateReparsePoints(string root)
    {
        var links = new List<FileSystemInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    links.Add(entry);
                    continue;
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }

        return links;
    }

    private static bool IsWorkspaceSelfLink(DirectoryInfo link)
    {
        if (link.Name is not ("backend" or "frontend"))
        {
            return false;
        }

        var normalized = link.FullName.Replace('/', '\\');
        var marker = Path.Combine("node_modules", ".pnpm", "node_modules", link.Name);
        return normalized.EndsWith(marker, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectoryPreserveLinksCore(
        DirectoryInfo source,
        DirectoryInfo destination)
    {
        destination.Create();
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            var targetPath = Path.Combine(destination.FullName, entry.Name);
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var linkTarget = entry.LinkTarget
                    ?? throw new IOException($"Could not read link target: {entry.FullName}");
                if (entry is DirectoryInfo)
                {
                    Directory.CreateSymbolicLink(targetPath, linkTarget);
                }
                else
                {
                    File.CreateSymbolicLink(targetPath, linkTarget);
                }
                continue;
            }

            if (entry is FileInfo file)
            {
                CopyFile(file.FullName, targetPath);
            }
            else if (entry is DirectoryInfo directory)
            {
                CopyDirectoryPreserveLinksCore(
                    directory,
                    new DirectoryInfo(targetPath));
            }
        }
    }

    private static void CopyDirectoryCore(DirectoryInfo source, DirectoryInfo destination)
    {
        if (source.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            source = source.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo
                ?? throw new IOException($"Could not resolve directory link: {source.FullName}");
        }

        destination.Create();
        foreach (var file in source.EnumerateFiles())
        {
            var actualFile = file;
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                actualFile = file.ResolveLinkTarget(returnFinalTarget: true) as FileInfo
                    ?? throw new IOException($"Could not resolve file link: {file.FullName}");
            }
            CopyFile(actualFile.FullName, Path.Combine(destination.FullName, file.Name));
        }

        foreach (var directory in source.EnumerateDirectories())
        {
            CopyDirectoryCore(
                directory,
                new DirectoryInfo(Path.Combine(destination.FullName, directory.Name)));
        }
    }

    private static void DeleteDirectoryCore(DirectoryInfo directory)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            directory.Delete();
            return;
        }

        foreach (var file in directory.EnumerateFiles())
        {
            file.IsReadOnly = false;
            file.Delete();
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            DeleteDirectoryCore(child);
        }

        directory.Attributes = FileAttributes.Normal;
        directory.Delete();
    }
}
