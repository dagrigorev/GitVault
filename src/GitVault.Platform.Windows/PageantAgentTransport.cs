using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using GitVault.Core.Ssh.Agent;

namespace GitVault.Platform.Windows;

/// <summary>
/// Pageant's legacy channel: the request is written into a named shared-memory block and the
/// block's name is handed to Pageant's hidden window through <c>WM_COPYDATA</c>. Pageant writes
/// its reply back into the same block.
/// </summary>
/// <remarks>
/// PuTTY 0.78 and later also expose a named pipe, which
/// <see cref="NamedPipeAgentTransport"/> handles and which should be preferred. This transport
/// exists for older Pageant builds and for the versions TortoiseGit still ships.
///
/// VERIFY: against a running Pageant. The window class and title, the 8192-byte block size and
/// the request identifier are PuTTY implementation details rather than a published contract.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PageantAgentTransport : ISshAgentTransport
{
    /// <summary>Window class and title Pageant registers.</summary>
    public const string WindowName = "Pageant";

    /// <summary>Size of the shared-memory block Pageant expects.</summary>
    public const int BlockSize = 8192;

    private const int AgentCopydataId = unchecked((int)0x804E50BA);

    private bool _disposed;

    /// <summary>True when a Pageant window is currently present.</summary>
    /// <returns><see langword="true"/> when Pageant is running.</returns>
    public static bool IsRunning() => FindWindow(WindowName, WindowName) != IntPtr.Zero;

    /// <inheritdoc/>
    public Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Length + 4 > BlockSize)
        {
            throw new SshAgentException(
                $"Pageant accepts at most {BlockSize} bytes and the request needs {request.Length}");
        }

        var window = FindWindow(WindowName, WindowName);
        if (window == IntPtr.Zero)
        {
            throw new SshAgentException("Pageant is not running");
        }

        // The block name must be unique per request; Pageant only checks that it can open it.
        var mapName = "PageantRequest" + Environment.CurrentManagedThreadId.ToString("x8", CultureInfo.InvariantCulture)
                      + Guid.NewGuid().ToString("N")[..8];

        using var mapping = MemoryMappedFile.CreateNew(mapName, BlockSize);
        using var accessor = mapping.CreateViewAccessor(0, BlockSize);

        var buffer = request.ToArray();
        accessor.WriteArray(0, buffer, 0, buffer.Length);

        if (!SendCopyData(window, mapName))
        {
            throw new SshAgentException("Pageant refused the request");
        }

        var header = new byte[4];
        accessor.ReadArray(0, header, 0, 4);

        var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        if (length <= 0 || length > BlockSize - 4)
        {
            throw new SshAgentException($"Pageant returned an implausible message length of {length}");
        }

        var payload = new byte[length];
        accessor.ReadArray(4, payload, 0, length);
        return Task.FromResult(payload);
    }

    /// <inheritdoc/>
    public void Dispose() => _disposed = true;

    /// <summary>True once the transport has been disposed.</summary>
    internal bool IsDisposed => _disposed;

    private static bool SendCopyData(IntPtr window, string mapName)
    {
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(mapName + "\0");
        var namePointer = Marshal.AllocHGlobal(nameBytes.Length);

        try
        {
            Marshal.Copy(nameBytes, 0, namePointer, nameBytes.Length);

            var data = new CopyDataStruct
            {
                DwData = new IntPtr(AgentCopydataId),
                CbData = nameBytes.Length,
                LpData = namePointer,
            };

            var structPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CopyDataStruct>());
            try
            {
                Marshal.StructureToPtr(data, structPointer, fDeleteOld: false);
                return SendMessage(window, WmCopyData, IntPtr.Zero, structPointer) != IntPtr.Zero;
            }
            finally
            {
                Marshal.DestroyStructure<CopyDataStruct>(structPointer);
                Marshal.FreeHGlobal(structPointer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(namePointer);
        }
    }

    private const int WmCopyData = 0x004A;

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr DwData;
        public int CbData;
        public IntPtr LpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

/// <summary>Creates the Windows-only agent transports.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentTransportFactory : ISshAgentTransportFactory
{
    /// <inheritdoc/>
    public bool CanHandle(AgentEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.Transport is AgentTransportKind.NamedPipe or AgentTransportKind.PageantWindow;
    }

    /// <inheritdoc/>
    public ISshAgentTransport Create(AgentEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.Transport switch
        {
            AgentTransportKind.NamedPipe => new NamedPipeAgentTransport(endpoint.Endpoint),
            AgentTransportKind.PageantWindow => new PageantAgentTransport(),
            _ => throw new SshAgentException($"Unsupported transport {endpoint.Transport}"),
        };
    }
}

/// <summary>
/// Windows agent endpoints: the Win32 OpenSSH pipe, whatever Pageant is exposing, 1Password's
/// pipe, gpg-agent's emulated socket, and anything <c>SSH_AUTH_SOCK</c> points at.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentEndpointProvider : IAgentEndpointProvider
{
    /// <summary>Pipe name the Win32 OpenSSH agent service listens on.</summary>
    public const string OpenSshPipeName = "openssh-ssh-agent";

    private readonly Core.Abstractions.IPlatformPaths _paths;

    /// <summary>Creates the provider.</summary>
    /// <param name="paths">Platform paths.</param>
    public WindowsAgentEndpointProvider(Core.Abstractions.IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc/>
    public IReadOnlyList<AgentEndpoint> GetEndpoints()
    {
        var endpoints = new List<AgentEndpoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(Core.Models.AgentKind kind, string endpoint, AgentTransportKind transport, bool writes = true)
        {
            if (!string.IsNullOrWhiteSpace(endpoint) && seen.Add(endpoint))
            {
                endpoints.Add(new AgentEndpoint(kind, endpoint, transport, writes));
            }
        }

        Add(Core.Models.AgentKind.OpenSshWindowsPipe, OpenSshPipeName, AgentTransportKind.NamedPipe);

        // Pageant's modern pipe name embeds a per-user hash that is not worth reproducing, so
        // the pipe namespace is enumerated instead. It is a real directory on Windows.
        foreach (var pipe in EnumeratePipes("pageant"))
        {
            Add(Core.Models.AgentKind.Pageant, pipe, AgentTransportKind.NamedPipe);
        }

        if (PageantAgentTransport.IsRunning())
        {
            Add(Core.Models.AgentKind.Pageant, PageantAgentTransport.WindowName, AgentTransportKind.PageantWindow);
        }

        foreach (var pipe in EnumeratePipes("1password"))
        {
            Add(Core.Models.AgentKind.OnePassword, pipe, AgentTransportKind.NamedPipe, writes: false);
        }

        var gpgSocket = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "gnupg",
            "S.gpg-agent.ssh");
        Add(Core.Models.AgentKind.GpgAgent, gpgSocket, AgentTransportKind.EmulatedSocket);

        var authSock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrWhiteSpace(authSock))
        {
            // On Windows this is usually a pipe name, but a WSL relay may set a socket path.
            var transport = authSock.Contains(@"\pipe\", StringComparison.OrdinalIgnoreCase)
                ? AgentTransportKind.NamedPipe
                : AgentTransportKind.UnixSocket;

            Add(Core.Models.AgentKind.Unknown, authSock, transport);
        }

        _ = _paths;
        return endpoints;
    }

    /// <summary>Lists named pipes whose name contains a marker.</summary>
    /// <param name="marker">Case-insensitive substring to match.</param>
    /// <returns>Matching pipe names.</returns>
    internal static IReadOnlyList<string> EnumeratePipes(string marker)
    {
        try
        {
            return
            [
                .. Directory.GetFiles(@"\\.\pipe\")
                    .Select(Path.GetFileName)
                    .Where(name => name is not null
                                   && name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    .Select(name => name!)
            ];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Current user's account name, used when building per-user pipe names.</summary>
    /// <returns>The account name, or null.</returns>
    internal static string? CurrentUserName()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
