using System.Runtime.InteropServices;
using GitVault.Core.Abstractions;
using GitVault.Core.Platform;
using GitVault.Core.Ssh;
using GitVault.Core.Ssh.Agent;
using GitVault.Platform.Linux;
using GitVault.Platform.MacOS;
using GitVault.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GitVault.App.Composition;

/// <summary>
/// The single place in the application that branches on the operating system. Everything
/// downstream depends only on the interfaces registered here.
/// </summary>
internal static class PlatformModule
{
    /// <summary>Registers the platform-specific service implementations for the current OS.</summary>
    /// <param name="services">Service collection to add to.</param>
    /// <param name="dataRoot">
    /// Directory to treat as the user's home, or null to use the real one. Everything GitVault
    /// reads and writes moves with it, which is what makes a demonstration or a destructive test
    /// run reach nothing of the user's own.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddPlatformServices(
        this IServiceCollection services,
        string? dataRoot = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (dataRoot is { Length: > 0 })
        {
            // Registered first so it wins: the platform's own paths are still added below for the
            // answers that are genuinely platform-shaped, and this is what everything resolves.
            services.AddSingleton<PlatformPathsBase>(new RelocatedPlatformPaths(dataRoot));
        }

        // An operating system vault is not addressed by a path, so a relocated root does not move
        // it. Leaving it registered would mean a run that claims to be self-contained still reads
        // the machine's real saved passwords — which is exactly what the switch exists to avoid,
        // whether it is being used for a screenshot or for a destructive test.
        var isolated = dataRoot is { Length: > 0 };

        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<PlatformPathsBase, WindowsPlatformPaths>();
            services.AddSingleton<IPlatformInfo, WindowsPlatformInfo>();
            services.AddSingleton<IShellLauncher, WindowsShellLauncher>();
            services.AddSingleton<IGitInstallHints, WindowsGitInstallHints>();
            services.AddSingleton<ISshToolHints, WindowsSshToolHints>();
            services.AddSingleton<IFilePermissionService, WindowsFilePermissionService>();
            services.AddSingleton<IAgentEndpointProvider, WindowsAgentEndpointProvider>();
            services.AddSingleton<ISshAgentTransportFactory, WindowsAgentTransportFactory>();
            if (!isolated)
            {
                services.AddSingleton<ICredentialVault, WindowsCredentialManagerVault>();
            }

            if (!isolated)
            {
                services.AddSingleton<ICredentialVault, GcmDpapiStoreVault>();
            }

        }
        else if (OperatingSystem.IsMacOS())
        {
            services.TryAddSingleton<PlatformPathsBase, MacPlatformPaths>();
            services.AddSingleton<IPlatformInfo, MacPlatformInfo>();
            services.AddSingleton<IShellLauncher, MacShellLauncher>();
            services.AddSingleton<IGitInstallHints, MacGitInstallHints>();
            services.AddSingleton<ISshToolHints, MacSshToolHints>();
            services.AddSingleton<IFilePermissionService, MacFilePermissionService>();
            services.AddSingleton<IAgentEndpointProvider, MacAgentEndpointProvider>();
            if (!isolated)
            {
                services.AddSingleton<ICredentialVault, MacKeychainVault>();
            }

        }
        else if (OperatingSystem.IsLinux())
        {
            services.TryAddSingleton<PlatformPathsBase, LinuxPlatformPaths>();
            services.AddSingleton<IPlatformInfo, LinuxPlatformInfo>();
            services.AddSingleton<IShellLauncher, LinuxShellLauncher>();
            services.AddSingleton<IGitInstallHints, LinuxGitInstallHints>();
            services.AddSingleton<ISshToolHints, LinuxSshToolHints>();
            services.AddSingleton<IFilePermissionService, LinuxFilePermissionService>();
            services.AddSingleton<IAgentEndpointProvider, LinuxAgentEndpointProvider>();
            if (!isolated)
            {
                services.AddSingleton<ICredentialVault, SecretServiceVault>();
            }

        }
        else
        {
            throw new PlatformNotSupportedException(
                $"GitVault does not support {RuntimeInformation.OSDescription}.");
        }

        services.AddSingleton<IPlatformPaths>(sp => sp.GetRequiredService<PlatformPathsBase>());
        return services;
    }
}
