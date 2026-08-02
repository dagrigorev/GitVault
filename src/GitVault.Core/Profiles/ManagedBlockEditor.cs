using System.Text;

namespace GitVault.Core.Profiles;

/// <summary>
/// Inserts, updates and removes GitVault's own block inside a file it does not own, such as
/// <c>~/.ssh/config</c>.
/// </summary>
/// <remarks>
/// The contract is narrow on purpose: **nothing outside the markers is ever modified**. The
/// file's existing content, its comments, its ordering and its line endings all survive
/// byte-for-byte, and removing a block returns the file to exactly what it was before the block
/// was added. Everything here is pure text: the caller decides when to write.
/// </remarks>
public static class ManagedBlockEditor
{
    /// <summary>Builds the opening marker for a profile.</summary>
    /// <param name="profileName">Profile name.</param>
    /// <returns>The marker line.</returns>
    public static string BeginMarker(string profileName) => $"# >>> GitVault managed: {profileName} >>>";

    /// <summary>Builds the closing marker for a profile.</summary>
    /// <param name="profileName">Profile name.</param>
    /// <returns>The marker line.</returns>
    public static string EndMarker(string profileName) => $"# <<< GitVault managed: {profileName} <<<";

    /// <summary>
    /// Returns <paramref name="content"/> with the profile's block set to <paramref name="blockBody"/>,
    /// replacing an existing block or appending a new one.
    /// </summary>
    /// <param name="content">Current file contents. May be null or empty.</param>
    /// <param name="profileName">Profile whose block to write.</param>
    /// <param name="blockBody">Body to place between the markers, without the markers themselves.</param>
    /// <returns>The new file contents.</returns>
    public static string Upsert(string? content, string profileName, string blockBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentNullException.ThrowIfNull(blockBody);

        var original = content ?? string.Empty;
        var newLine = DetectNewLine(original);
        var block = BuildBlock(profileName, blockBody, newLine);

        if (TryFindBlock(original, profileName, out var start, out var end))
        {
            // Replace exactly the marked span; everything on either side is untouched.
            return original[..start] + block + original[end..];
        }

        if (original.Length == 0)
        {
            return block;
        }

        // Only ever add a line terminator, never a blank separator line. A blank line would be
        // indistinguishable from one the user wrote, and removal could then not tell whether to
        // take it back — which would break the guarantee that add-then-remove is the identity.
        //
        // The single documented side effect: a file that did not end with a newline gains one.
        return original.EndsWith('\n') ? original + block : original + newLine + block;
    }

    /// <summary>Returns <paramref name="content"/> with the profile's block removed.</summary>
    /// <param name="content">Current file contents.</param>
    /// <param name="profileName">Profile whose block to remove.</param>
    /// <returns>The new file contents, or the input unchanged when no block was present.</returns>
    public static string Remove(string? content, string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        var original = content ?? string.Empty;
        if (!TryFindBlock(original, profileName, out var start, out var end))
        {
            return original;
        }

        // Nothing to take back: Upsert only ever added a line terminator, and a file that had
        // one keeps it.
        return original[..start] + original[end..];
    }

    /// <summary>True when the file already carries a block for the profile.</summary>
    /// <param name="content">File contents.</param>
    /// <param name="profileName">Profile to look for.</param>
    /// <returns><see langword="true"/> when a complete block is present.</returns>
    public static bool ContainsBlock(string? content, string profileName) =>
        TryFindBlock(content ?? string.Empty, profileName, out _, out _);

    /// <summary>Extracts the body of the profile's block.</summary>
    /// <param name="content">File contents.</param>
    /// <param name="profileName">Profile to look for.</param>
    /// <returns>The body between the markers, or null when there is no block.</returns>
    public static string? ReadBlockBody(string? content, string profileName)
    {
        var original = content ?? string.Empty;
        if (!TryFindBlock(original, profileName, out var start, out var end))
        {
            return null;
        }

        var block = original[start..end];
        var begin = BeginMarker(profileName);
        var finish = EndMarker(profileName);

        var bodyStart = block.IndexOf(begin, StringComparison.Ordinal) + begin.Length;
        var bodyEnd = block.LastIndexOf(finish, StringComparison.Ordinal);

        return bodyEnd <= bodyStart ? string.Empty : block[bodyStart..bodyEnd].Trim('\r', '\n');
    }

    /// <summary>Renders an SSH <c>Host</c> block body from a host alias.</summary>
    /// <param name="alias">Alias to render.</param>
    /// <param name="newLine">Line ending to use.</param>
    /// <returns>The body, without markers.</returns>
    public static string RenderHostAlias(Models.SshHostAlias alias, string newLine = "\n")
    {
        ArgumentNullException.ThrowIfNull(alias);

        var builder = new StringBuilder();
        builder.Append("Host ").Append(alias.Alias).Append(newLine);
        builder.Append("    HostName ").Append(alias.HostName).Append(newLine);

        if (!string.IsNullOrWhiteSpace(alias.User))
        {
            builder.Append("    User ").Append(alias.User).Append(newLine);
        }

        if (!string.IsNullOrWhiteSpace(alias.IdentityFile))
        {
            builder.Append("    IdentityFile ").Append(alias.IdentityFile).Append(newLine);
        }

        if (alias.IdentitiesOnly)
        {
            builder.Append("    IdentitiesOnly yes").Append(newLine);
        }

        foreach (var (key, value) in alias.ExtraOptions)
        {
            builder.Append("    ").Append(key).Append(' ').Append(value).Append(newLine);
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>Finds the span covering the whole block, markers included.</summary>
    /// <param name="content">File contents.</param>
    /// <param name="profileName">Profile to look for.</param>
    /// <param name="start">Index the block starts at.</param>
    /// <param name="end">Index just past the block.</param>
    /// <returns><see langword="true"/> when a complete, well-ordered block was found.</returns>
    private static bool TryFindBlock(string content, string profileName, out int start, out int end)
    {
        start = 0;
        end = 0;

        var begin = BeginMarker(profileName);
        var finish = EndMarker(profileName);

        var beginIndex = content.IndexOf(begin, StringComparison.Ordinal);
        if (beginIndex < 0)
        {
            return false;
        }

        var endIndex = content.IndexOf(finish, beginIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            // An opening marker with no closing one means someone edited the file by hand.
            // Refusing to act is safer than guessing where the block was meant to stop.
            return false;
        }

        start = beginIndex;
        end = endIndex + finish.Length;

        // Take the newline that terminates the closing marker, so removal leaves no blank line.
        if (end < content.Length && content[end] == '\r')
        {
            end++;
        }

        if (end < content.Length && content[end] == '\n')
        {
            end++;
        }

        return true;
    }

    private static string BuildBlock(string profileName, string blockBody, string newLine)
    {
        var builder = new StringBuilder();
        builder.Append(BeginMarker(profileName)).Append(newLine);

        var body = blockBody.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n');
        if (body.Length > 0)
        {
            foreach (var line in body.Split('\n'))
            {
                builder.Append(line).Append(newLine);
            }
        }

        builder.Append(EndMarker(profileName)).Append(newLine);
        return builder.ToString();
    }

    /// <summary>Detects the dominant line ending so an edit does not convert the file.</summary>
    /// <param name="content">File contents.</param>
    /// <returns><c>\r\n</c> or <c>\n</c>.</returns>
    internal static string DetectNewLine(string content)
    {
        var crlf = 0;
        var index = content.IndexOf("\r\n", StringComparison.Ordinal);
        while (index >= 0)
        {
            crlf++;
            index = content.IndexOf("\r\n", index + 2, StringComparison.Ordinal);
        }

        var lf = content.Count(c => c == '\n') - crlf;
        return crlf > lf ? "\r\n" : "\n";
    }
}
