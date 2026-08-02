using FluentAssertions;
using Xunit;

namespace GitVault.Core.Tests;

/// <summary>What <c>ssh-keygen -lf</c> reported for one fixture key.</summary>
/// <param name="Name">Fixture base name.</param>
/// <param name="Bits">Key size in bits.</param>
/// <param name="Sha256">Canonical SHA-256 fingerprint, including the prefix.</param>
/// <param name="Md5">Legacy MD5 fingerprint, or <c>-</c> when not recorded.</param>
/// <param name="Algorithm">Algorithm name as ssh-keygen prints it.</param>
internal sealed record ExpectedFingerprint(string Name, int Bits, string Sha256, string Md5, string Algorithm);

/// <summary>Locates the committed SSH fixtures and the reference fingerprint manifest.</summary>
internal static class SshFixtures
{
    private static readonly Lazy<string> RootValue = new(FindRoot);

    private static readonly Lazy<IReadOnlyDictionary<string, ExpectedFingerprint>> ExpectedValue =
        new(LoadExpected);

    /// <summary>Directory holding the fixtures.</summary>
    internal static string Root => RootValue.Value;

    /// <summary>Reference fingerprints, keyed by fixture base name.</summary>
    internal static IReadOnlyDictionary<string, ExpectedFingerprint> Expected => ExpectedValue.Value;

    /// <summary>Absolute path of a fixture file.</summary>
    /// <param name="name">File name inside the fixture directory.</param>
    /// <returns>The path.</returns>
    internal static string Path(string name) => System.IO.Path.Combine(Root, name);

    /// <summary>Reads a fixture file as text.</summary>
    /// <param name="name">File name inside the fixture directory.</param>
    /// <returns>The contents.</returns>
    internal static string Text(string name) => File.ReadAllText(Path(name));

    /// <summary>Names of every fixture that has a reference fingerprint.</summary>
    /// <returns>Test data rows.</returns>
    internal static TheoryData<string> AllKeyNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Expected.Keys.Where(k => !k.EndsWith(".ppk", StringComparison.Ordinal)))
        {
            data.Add(name);
        }

        return data;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "GitVault.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests must run from inside the repository");

        var fixtures = System.IO.Path.Combine(directory!.FullName, "tests", "fixtures", "ssh");
        Directory.Exists(fixtures).Should().BeTrue($"fixtures must exist at {fixtures}");
        return fixtures;
    }

    private static IReadOnlyDictionary<string, ExpectedFingerprint> LoadExpected()
    {
        var result = new Dictionary<string, ExpectedFingerprint>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(System.IO.Path.Combine(Root, "expected-fingerprints.tsv")))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 5)
            {
                continue;
            }

            result[parts[0]] = new ExpectedFingerprint(
                parts[0],
                int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                parts[2],
                parts[3],
                parts[4]);
        }

        result.Should().NotBeEmpty();
        return result;
    }
}
