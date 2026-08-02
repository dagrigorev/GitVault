using System.Globalization;
using System.IO.Compression;
using System.Text;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Diagnostics;

/// <summary>One file that would go into a diagnostics bundle.</summary>
/// <param name="Name">Name it will have inside the archive.</param>
/// <param name="Description">One line saying what it is, shown to the user before writing.</param>
/// <param name="Content">The exact bytes that will be written, already redacted.</param>
public sealed record DiagnosticsItem(string Name, string Description, string Content)
{
    /// <summary>Size the entry will occupy, for the preview.</summary>
    public int Length => Encoding.UTF8.GetByteCount(Content);
}

/// <summary>Builds a support bundle the user can attach to a bug report.</summary>
public interface IDiagnosticsBundleBuilder
{
    /// <summary>
    /// Assembles everything the bundle would contain, without writing anything.
    /// </summary>
    /// <remarks>
    /// The UI shows this list, with the exact content of each entry, before the user decides
    /// whether to save it. A bundle nobody has read is a bundle nobody should send.
    /// </remarks>
    /// <param name="report">Last scan report, when there has been one.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The entries, in the order they will appear.</returns>
    Task<IReadOnlyList<DiagnosticsItem>> PreviewAsync(DiscoveryReport report, CancellationToken cancellationToken);

    /// <summary>Writes previously previewed entries to a zip archive.</summary>
    /// <param name="items">Entries from <see cref="PreviewAsync"/>.</param>
    /// <param name="destinationPath">Archive to create.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once the archive exists.</returns>
    Task WriteAsync(
        IReadOnlyList<DiagnosticsItem> items,
        string destinationPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// Collects redacted logs, environment facts, the probe status matrix and a configuration
/// inventory.
/// </summary>
/// <remarks>
/// The inventory lists **keys and origins only, never values**. A git configuration can contain
/// a URL with an embedded token, a proxy password or a private path; the names of the settings
/// are enough to diagnose a problem, and the values are not ours to hand over. Log files go
/// through the same redactor the live sinks use, a second time, because a bundle leaves the
/// machine and a log file does not.
/// </remarks>
public sealed class DiagnosticsBundleBuilder : IDiagnosticsBundleBuilder
{
    private const int MaxLogFiles = 5;
    private const int MaxLogBytes = 2 * 1024 * 1024;

    private readonly IPlatformPaths _paths;
    private readonly IPlatformInfo _platformInfo;
    private readonly IGitConfigService _config;
    private readonly ISecretRedactor _redactor;

    /// <summary>Creates the builder.</summary>
    /// <param name="paths">Platform paths.</param>
    /// <param name="platformInfo">Platform facts.</param>
    /// <param name="config">Git configuration service, for the inventory.</param>
    /// <param name="redactor">Redactor applied to every log line.</param>
    public DiagnosticsBundleBuilder(
        IPlatformPaths paths,
        IPlatformInfo platformInfo,
        IGitConfigService config,
        ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(platformInfo);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(redactor);

        _paths = paths;
        _platformInfo = platformInfo;
        _config = config;
        _redactor = redactor;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DiagnosticsItem>> PreviewAsync(
        DiscoveryReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var items = new List<DiagnosticsItem>
        {
            new("environment.txt", "Operating system, runtime and GitVault version.", BuildEnvironment()),
            new("probe-status.tsv", "Which probes ran, what they reported and how long they took.",
                BuildProbeMatrix(report)),
            new("summary.tsv", "How many artifacts of each kind were found. No names or values.",
                BuildSummary(report)),
        };

        var inventory = await BuildConfigInventoryAsync(cancellationToken).ConfigureAwait(false);
        items.Add(new DiagnosticsItem(
            "git-config-inventory.tsv",
            "Names and origins of git settings. Values are deliberately excluded.",
            inventory));

        foreach (var log in CollectLogs(cancellationToken))
        {
            items.Add(log);
        }

        return items;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(
        IReadOnlyList<DiagnosticsItem> items,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(destinationPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = archive.CreateEntry(item.Name, CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
            await writer.WriteAsync(item.Content.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Builds the archive file name for a bundle taken at a point in time.</summary>
    /// <param name="takenUtc">When the bundle was assembled.</param>
    /// <returns>A file name, without a directory.</returns>
    public static string BuildFileName(DateTimeOffset takenUtc) =>
        "gitvault-diagnostics-"
        + takenUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
        + ".zip";

    /// <summary>Builds the environment description.</summary>
    /// <returns>The file content.</returns>
    internal string BuildEnvironment()
    {
        var builder = new StringBuilder();
        builder.Append("gitvault.version\t")
            .Append(typeof(DiagnosticsBundleBuilder).Assembly.GetName().Version?.ToString() ?? "unknown")
            .Append('\n');
        builder.Append("os.description\t").Append(_platformInfo.OsDescription).Append('\n');
        builder.Append("os.platform\t").Append(_platformInfo.PlatformId).Append('\n');
        builder.Append("os.architecture\t").Append(_platformInfo.Architecture).Append('\n');
        builder.Append("os.elevated\t").Append(_platformInfo.IsElevated).Append('\n');
        builder.Append("runtime.version\t").Append(System.Environment.Version).Append('\n');
        builder.Append("culture\t").Append(CultureInfo.CurrentUICulture.Name).Append('\n');
        builder.Append("git.found\t").Append(_config.HasGitBinary).Append('\n');
        builder.Append("git.version\t").Append(_config.GitVersion ?? "n/a").Append('\n');

        // The path is included because "which git" is a common cause of confusion; it is a
        // program location, not user data.
        builder.Append("git.path\t").Append(_config.GitBinaryPath ?? "n/a").Append('\n');

        return builder.ToString();
    }

    /// <summary>Builds the probe status matrix.</summary>
    /// <param name="report">Scan report.</param>
    /// <returns>The file content.</returns>
    internal string BuildProbeMatrix(DiscoveryReport report)
    {
        var builder = new StringBuilder("probe\tstatus\telapsed_ms\tdiagnostics\n");

        foreach (var status in report.ProbeStatuses.OrderBy(s => s.ProbeId, StringComparer.Ordinal))
        {
            builder.Append(status.ProbeId).Append('\t')
                .Append(status.Status).Append('\t')
                .Append(status.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)).Append('\t')
                .Append(_redactor.Redact(status.Diagnostics).Replace('\n', ' ')).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Builds the counts-only summary.</summary>
    /// <param name="report">Scan report.</param>
    /// <returns>The file content.</returns>
    internal static string BuildSummary(DiscoveryReport report)
    {
        var builder = new StringBuilder("kind\tcount\n");
        builder.Append("identities\t").Append(report.Identities.Count).Append('\n');
        builder.Append("keys\t").Append(report.Keys.Count).Append('\n');
        builder.Append("agents\t").Append(report.Agents.Count).Append('\n');
        builder.Append("credentials\t").Append(report.Credentials.Count).Append('\n');
        builder.Append("clients\t").Append(report.Clients.Count).Append('\n');
        builder.Append("warnings\t").Append(report.Warnings.Count).Append('\n');

        foreach (var group in report.Warnings.GroupBy(w => w.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            builder.Append("warning.").Append(group.Key).Append('\t').Append(group.Count()).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the configuration inventory: key and origin, never the value.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file content.</returns>
    internal async Task<string> BuildConfigInventoryAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder("key\tscope\torigin\tvalue_length\n");

        IReadOnlyList<GitConfigValue> values;
        try
        {
            values = await _config.ListAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Git.GitConfigException)
        {
            return builder.Append("# git configuration could not be read\n").ToString();
        }

        foreach (var value in values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            // The length is diagnostic (an empty value is a common cause of confusion) while
            // revealing nothing about the content.
            builder.Append(value.Key).Append('\t')
                .Append(value.Scope).Append('\t')
                .Append(_redactor.Redact(value.Origin)).Append('\t')
                .Append(value.Value.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return builder.ToString();
    }

    private IEnumerable<DiagnosticsItem> CollectLogs(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> files;
        try
        {
            files = Directory.Exists(_paths.LogDirectory)
                ? [.. Directory.EnumerateFiles(_paths.LogDirectory, "*.log")
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .Take(MaxLogFiles)]
                : [];
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string text;
            try
            {
                var info = new FileInfo(file);
                using var stream = File.OpenRead(file);

                // Take the tail of a long log: the end is where the problem is.
                if (info.Length > MaxLogBytes)
                {
                    stream.Seek(info.Length - MaxLogBytes, SeekOrigin.Begin);
                }

                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            yield return new DiagnosticsItem(
                "logs/" + Path.GetFileName(file),
                "Application log, passed through the secret redactor again before export.",
                _redactor.Redact(text));
        }
    }
}
