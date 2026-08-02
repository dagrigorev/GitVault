using System.Runtime.Versioning;
using GitVault.Core.Models;
using Microsoft.Win32;

namespace GitVault.Clients;

/// <summary>
/// TortoiseGit, which stores its settings in the registry under <c>HKCU\Software\TortoiseGit</c>.
/// </summary>
/// <remarks>
/// The interesting part is the SSH wiring: TortoiseGit drives TortoisePlink with PuTTY-format
/// keys, and binds a <c>.ppk</c> per remote. Surfacing which remote uses which key is the thing a
/// TortoiseGit user cannot easily see for themselves.
///
/// VERIFY: the per-remote value names against a current TortoiseGit. They are stable in practice
/// but undocumented.
/// </remarks>
public sealed class TortoiseGitProbe : ClientProbeBase
{
    private const string RegistryRoot = @"Software\TortoiseGit";

    /// <summary>Creates the probe.</summary>
    /// <param name="environment">Filesystem to look at.</param>
    public TortoiseGitProbe(IClientEnvironment environment)
        : base(environment)
    {
    }

    /// <inheritdoc/>
    public override GitClientKind ClientKind => GitClientKind.TortoiseGit;

    /// <inheritdoc/>
    public override string DisplayName => "TortoiseGit";

    /// <inheritdoc/>
    public override bool IsSupportedOnThisPlatform => OperatingSystem.IsWindows();

    /// <summary>
    /// Registry-backed settings, exposed for testing. The default implementation reads
    /// <c>HKCU</c>; a test supplies a dictionary instead.
    /// </summary>
    internal Func<IReadOnlyDictionary<string, string>>? SettingsOverride { get; set; }

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateConfigRoots()
    {
        // TortoiseGit keeps almost everything in the registry, but its cache directory is a real
        // path and its presence is what proves an installation rather than a leftover key.
        yield return Path.Combine(Environment.LocalAppData, "TortoiseGit");
        yield return Path.Combine(Environment.AppData, "TortoiseGit");
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateInstallPaths()
    {
        yield return Path.Combine(Environment.ProgramFiles, "TortoiseGit");
        yield return Path.Combine(Environment.ProgramFilesX86, "TortoiseGit");
    }

    /// <inheritdoc/>
    protected override ClientReadResult ReadConfiguration(IReadOnlyList<string> roots)
    {
        var settings = SettingsOverride?.Invoke()
                       ?? (OperatingSystem.IsWindows()
                           ? ReadRegistrySettings()
                           : new Dictionary<string, string>());
        if (settings.Count == 0)
        {
            return ClientReadResult.Opaque;
        }

        var boundKeys = ExtractRemoteKeyBindings(settings);
        var warnings = new List<KeyWarning>();

        foreach (var (remote, keyFile) in boundKeys)
        {
            if (!Environment.FileExists(keyFile))
            {
                warnings.Add(new KeyWarning(MissingBoundKeyCode, WarningSeverity.Medium, $"{remote}: {keyFile}"));
            }
        }

        var sshExecutable = settings.GetValueOrDefault("SSH")
                            ?? settings.GetValueOrDefault("SSHClient");

        var sshConfig = new ClientSshConfig(
            sshExecutable,
            UsesPageant(sshExecutable) ? AgentKind.Pageant : null)
        {
            BoundKeyFiles = boundKeys,
            UsesPuttyKeys = true,
        };

        return new ClientReadResult
        {
            SshConfiguration = sshConfig,
            Warnings = warnings,
            IsOpaque = false,
        };
    }

    /// <summary>Warning code raised when a remote points at a key file that is gone.</summary>
    public const string MissingBoundKeyCode = "TortoiseGitMissingKey";

    /// <summary>
    /// Pulls the per-remote key bindings out of the flat settings map. TortoiseGit stores them
    /// as <c>Remote\&lt;name&gt;\puttykeyfile</c>.
    /// </summary>
    /// <param name="settings">Flattened registry values.</param>
    /// <returns>Remote name to key file.</returns>
    internal static IReadOnlyDictionary<string, string> ExtractRemoteKeyBindings(
        IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in settings)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = key.Replace('/', '\\');
            if (!normalized.StartsWith(@"Remote\", StringComparison.OrdinalIgnoreCase)
                || !normalized.EndsWith("puttykeyfile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remote = normalized[@"Remote\".Length..];
            var separator = remote.LastIndexOf('\\');
            if (separator > 0)
            {
                remote = remote[..separator];
            }

            result[remote] = value;
        }

        return result;
    }

    private static bool UsesPageant(string? sshExecutable) =>
        sshExecutable is not null
        && (sshExecutable.Contains("plink", StringComparison.OrdinalIgnoreCase)
            || sshExecutable.Contains("TortoisePlink", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Flattens <c>HKCU\Software\TortoiseGit</c> into a path-keyed map.
    /// </summary>
    /// <remarks>
    /// Only this method and its helper touch Windows APIs, which is why the annotation sits here
    /// rather than on the class: the rest of the probe, including the binding parser, is portable
    /// and therefore testable on every platform.
    /// </remarks>
    /// <returns>The settings, or an empty map when the key is absent or unreadable.</returns>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyDictionary<string, string> ReadRegistrySettings()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(RegistryRoot);
            if (root is null)
            {
                return values;
            }

            Collect(root, prefix: string.Empty, values, depth: 0);
        }
        catch (System.Security.SecurityException)
        {
            // No read access: the caller reports the client as detected but unreadable.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
        catch (IOException)
        {
            // Key removed while we were reading it.
        }

        return values;
    }

    [SupportedOSPlatform("windows")]
    private static void Collect(RegistryKey key, string prefix, Dictionary<string, string> values, int depth)
    {
        // TortoiseGit's tree is shallow; the limit exists so a corrupt hive cannot spin us.
        if (depth > 4)
        {
            return;
        }

        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(name);
            if (value is not null)
            {
                values[prefix + name] = value.ToString() ?? string.Empty;
            }
        }

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            using var subKey = key.OpenSubKey(subKeyName);
            if (subKey is not null)
            {
                Collect(subKey, prefix + subKeyName + "\\", values, depth + 1);
            }
        }
    }
}
