using GitVault.Core.Models;

namespace GitVault.Core.Diagnostics;

/// <summary>
/// Result of running a probe. Probe failures are values, not exceptions: a probe that
/// throws is caught by the orchestrator and turned into a <see cref="ProbeStatus.Failed"/> result.
/// </summary>
/// <typeparam name="T">Payload type produced on success.</typeparam>
public sealed class ProbeResult<T>
{
    private ProbeResult(string probeId, ProbeStatus status, T? value, string? diagnostics, TimeSpan elapsed)
    {
        ProbeId = probeId;
        Status = status;
        Value = value;
        Diagnostics = diagnostics;
        Elapsed = elapsed;
    }

    /// <summary>Identifier of the probe that produced this result.</summary>
    public string ProbeId { get; }

    /// <summary>What happened.</summary>
    public ProbeStatus Status { get; }

    /// <summary>Payload, present only when <see cref="Status"/> is <see cref="ProbeStatus.Ok"/>.</summary>
    public T? Value { get; }

    /// <summary>Redacted diagnostic text explaining a non-Ok status.</summary>
    public string? Diagnostics { get; }

    /// <summary>Wall-clock time the probe took.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>True when the probe produced a usable payload.</summary>
    public bool IsSuccess => Status == ProbeStatus.Ok && Value is not null;

    /// <summary>Creates a successful result.</summary>
    /// <param name="probeId">Probe identifier.</param>
    /// <param name="value">Payload.</param>
    /// <param name="elapsed">Time taken.</param>
    /// <returns>A successful result.</returns>
    public static ProbeResult<T> Ok(string probeId, T value, TimeSpan elapsed = default) =>
        new(probeId, ProbeStatus.Ok, value, null, elapsed);

    /// <summary>Creates a non-successful result.</summary>
    /// <param name="probeId">Probe identifier.</param>
    /// <param name="status">Failure status.</param>
    /// <param name="diagnostics">Redacted explanation.</param>
    /// <param name="elapsed">Time taken.</param>
    /// <returns>A failed result.</returns>
    public static ProbeResult<T> Fail(
        string probeId,
        ProbeStatus status,
        string? diagnostics = null,
        TimeSpan elapsed = default) =>
        new(probeId, status, default, diagnostics, elapsed);

    /// <summary>Projects the payload while keeping status and diagnostics.</summary>
    /// <typeparam name="TOut">Target payload type.</typeparam>
    /// <param name="selector">Projection applied when the result is successful.</param>
    /// <returns>A result of the projected type.</returns>
    public ProbeResult<TOut> Map<TOut>(Func<T, TOut> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return IsSuccess
            ? ProbeResult<TOut>.Ok(ProbeId, selector(Value!), Elapsed)
            : ProbeResult<TOut>.Fail(ProbeId, Status, Diagnostics, Elapsed);
    }
}

/// <summary>Status of one probe within a scan, independent of its payload type.</summary>
/// <param name="ProbeId">Probe identifier.</param>
/// <param name="DisplayName">Name to show in the status matrix. Product names are not localized.</param>
/// <param name="Status">What happened.</param>
/// <param name="Diagnostics">Redacted explanation of a non-Ok status.</param>
/// <param name="Elapsed">Time taken.</param>
public sealed record ProbeStatusEntry(
    string ProbeId,
    string DisplayName,
    ProbeStatus Status,
    string? Diagnostics,
    TimeSpan Elapsed);
