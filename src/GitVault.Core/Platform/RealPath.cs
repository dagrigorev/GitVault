namespace GitVault.Core.Platform;

/// <summary>
/// Resolves a path to the one the operating system actually stores it under, following every
/// symbolic link along the way.
/// </summary>
/// <remarks>
/// <see cref="Path.GetFullPath(string)"/> makes a path absolute and tidies it up, but it never
/// follows a link: given <c>/var/folders/x</c> on macOS it returns <c>/var/folders/x</c>, while
/// git — which resolves before it records anything — reports <c>/private/var/folders/x</c>. The
/// two strings describe the same directory and compare as different, so a path that came from a
/// folder picker stops matching the one git printed, and GitVault says it cannot find a working
/// tree that is plainly there.
///
/// macOS is where this is unavoidable, because <c>/var</c> and <c>/tmp</c> are links on every
/// installation. It is not only macOS: a Linux home under a symlinked mount and a Windows
/// directory junction produce the same disagreement.
///
/// A path that does not exist, or that cannot be read, is returned as its plain absolute form.
/// Resolution is a best effort to make two names for one directory compare equal — never a
/// precondition for using the path.
/// </remarks>
public static class RealPath
{
    /// <summary>How many links deep to follow before giving up.</summary>
    /// <remarks>
    /// A link's target may itself run through further links, and two links can be made to point
    /// at each other. The limit is what keeps a cycle from becoming a hang.
    /// </remarks>
    private const int MaxHops = 40;

    /// <summary>Resolves a path through any symbolic links in it.</summary>
    /// <param name="path">Path to resolve. May be relative.</param>
    /// <returns>The resolved absolute path, or the absolute path when resolution is not possible.</returns>
    public static string Resolve(string? path) => Resolve(path, MaxHops);

    private static string Resolve(string? path, int hopsLeft)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path ?? string.Empty;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (ArgumentException)
        {
            return path;
        }
        catch (NotSupportedException)
        {
            return path;
        }
        catch (PathTooLongException)
        {
            return path;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        var remainder = full[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var segment in remainder)
        {
            current = Path.Combine(current, segment);

            var followed = FollowLink(current);
            if (!string.Equals(followed, current, StringComparison.Ordinal))
            {
                // A link records whatever target was written when it was made, and that target may
                // run through links of its own: on macOS a link created against /var/… names
                // /var/…, and returning it verbatim would undo the resolution already done.
                current = hopsLeft > 0 ? Resolve(followed, hopsLeft - 1) : followed;
            }
        }

        return current.Length == 0 ? full : current;
    }

    /// <summary>
    /// Compares two paths as names for the same file or directory, resolving both first.
    /// </summary>
    /// <param name="left">One path.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when they name the same thing.</returns>
    public static bool AreSame(string? left, string? right) =>
        string.Equals(
            Resolve(left).TrimEnd(Path.DirectorySeparatorChar),
            Resolve(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Replaces one path segment with its link target, when it is a link.</summary>
    /// <param name="candidate">Absolute path built so far.</param>
    /// <returns>The target when it is a link, otherwise the input.</returns>
    private static string FollowLink(string candidate)
    {
        try
        {
            // A segment that does not exist cannot be a link, and asking would only throw.
            var info = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate) ? (FileSystemInfo)new FileInfo(candidate) : null;

            if (info?.LinkTarget is null)
            {
                return candidate;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                return candidate;
            }

            // A relative target is relative to the directory the link lives in, not to the
            // process's working directory.
            return Path.IsPathRooted(target.FullName)
                ? target.FullName
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(candidate) ?? string.Empty, target.FullName));
        }
        catch (IOException)
        {
            return candidate;
        }
        catch (UnauthorizedAccessException)
        {
            return candidate;
        }
    }
}
