using System.Runtime.Versioning;
using GitVault.Core.Platform;

namespace GitVault.Platform.Linux;

/// <summary>POSIX permission handling on Linux.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxFilePermissionService : PosixFilePermissionService;
