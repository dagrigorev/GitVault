using System.Text.RegularExpressions;
using GitVault.Core.Abstractions;

namespace GitVault.Core.Security;

/// <summary>
/// Pattern-based redactor. Deliberately over-eager: a false positive costs a log line's
/// readability, a false negative leaks a credential.
/// </summary>
public sealed partial class SecretRedactor : ISecretRedactor
{
    /// <summary>Text substituted in place of anything that looks like a secret.</summary>
    public const string Placeholder = "[REDACTED]";

    private static readonly Regex[] Patterns =
    [
        PemBlockRegex(),
        PuttyPrivateLinesRegex(),
        AssignmentRegex(),
        GitCredentialsUrlRegex(),
        BearerRegex(),
        VendorTokenRegex(),
        LongBase64Regex(),
    ];

    /// <inheritdoc/>
    public string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var result = input;
        foreach (var pattern in Patterns)
        {
            result = pattern.Replace(result, Replace);
        }

        return result;
    }

    /// <inheritdoc/>
    public bool ContainsSecret(string? input) =>
        !string.IsNullOrEmpty(input) && !string.Equals(Redact(input), input, StringComparison.Ordinal);

    private static string Replace(Match match)
    {
        // Group "keep" lets a pattern preserve a non-secret prefix such as "password=".
        var keep = match.Groups["keep"];
        return keep.Success ? keep.Value + Placeholder : Placeholder;
    }

    [GeneratedRegex(
        "-----BEGIN[^-]*PRIVATE KEY-----.*?-----END[^-]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex PemBlockRegex();

    [GeneratedRegex(
        @"(?<keep>(?:Private-Lines|Private-MAC|Argon2-Salt)\s*:\s*)\S(?:.|\n(?!\w+\s*:))*",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex PuttyPrivateLinesRegex();

    [GeneratedRegex(
        @"(?<keep>\b(?:pass(?:word|phrase)?|secret|token|api[_-]?key|auth|credential|pat)\b\s*[:=]\s*)(?:""[^""]*""|'[^']*'|\S+)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex AssignmentRegex();

    [GeneratedRegex(
        @"(?<keep>[a-z][a-z0-9+.-]*://[^\s:/@]+:)[^\s@]+(?=@)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex GitCredentialsUrlRegex();

    [GeneratedRegex(
        @"(?<keep>\bBearer\s+)[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex BearerRegex();

    // GitHub (ghp_/gho_/ghu_/ghs_/ghr_/github_pat_), GitLab (glpat-), Atlassian (ATATT).
    [GeneratedRegex(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_-]{16,}|ATATT[A-Za-z0-9_\-=]{16,})",
        RegexOptions.None,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex VendorTokenRegex();

    // Long unbroken base64 runs: key blobs pasted into a message.
    [GeneratedRegex(
        @"\b[A-Za-z0-9+/]{60,}={0,2}\b",
        RegexOptions.None,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex LongBase64Regex();
}
