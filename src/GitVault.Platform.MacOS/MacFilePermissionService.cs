using System.Runtime.Versioning;
using GitVault.Core.Platform;

namespace GitVault.Platform.MacOS;

/// <summary>POSIX permission handling on macOS.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacFilePermissionService : PosixFilePermissionService;
