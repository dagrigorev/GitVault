using System.IO.Pipes;
using System.Runtime.Versioning;
using GitVault.Core.Ssh.Agent;

namespace GitVault.Platform.Windows;

/// <summary>
/// Talks to an agent listening on a Windows named pipe. Win32 OpenSSH uses
/// <c>\\.\pipe\openssh-ssh-agent</c>; Pageant 0.78 and later, and 1Password when configured for
/// Windows, use pipes too.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeAgentTransport : StreamAgentTransport
{
    private const int ConnectTimeoutMilliseconds = 3000;

    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;

    /// <summary>Creates the transport.</summary>
    /// <param name="pipeName">Pipe name, with or without the <c>\\.\pipe\</c> prefix.</param>
    public NamedPipeAgentTransport(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = Normalize(pipeName);
    }

    /// <summary>Strips the <c>\\.\pipe\</c> prefix that <see cref="NamedPipeClientStream"/> adds back.</summary>
    /// <param name="pipeName">Raw pipe name.</param>
    /// <returns>The bare name.</returns>
    internal static string Normalize(string pipeName)
    {
        const string Prefix = @"\\.\pipe\";
        return pipeName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? pipeName[Prefix.Length..]
            : pipeName;
    }

    /// <inheritdoc/>
    protected override async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new SshAgentException($"No agent is listening on {_pipeName}", ex);
        }
        catch (IOException ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new SshAgentException($"Could not open the agent pipe {_pipeName}", ex);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _pipe = pipe;
        return pipe;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pipe?.Dispose();
            _pipe = null;
        }

        base.Dispose(disposing);
    }
}
