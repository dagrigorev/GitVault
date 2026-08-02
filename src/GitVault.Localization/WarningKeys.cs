namespace GitVault.Localization;

/// <summary>
/// Builds resource keys for health findings from their stable code. A warning raised anywhere in
/// the domain only carries a code; the two strings that explain it to a human live here.
/// </summary>
public static class WarningKeys
{
    /// <summary>Resource key of a warning's one-line title.</summary>
    /// <param name="code">Stable warning code, e.g. <c>KeyWorldReadable</c>.</param>
    /// <returns>A resource key.</returns>
    public static string Title(string code) => "Warning_" + code + "_Title";

    /// <summary>Resource key of a warning's "what does this mean?" explanation.</summary>
    /// <param name="code">Stable warning code.</param>
    /// <returns>A resource key.</returns>
    public static string Body(string code) => "Warning_" + code + "_Body";
}
