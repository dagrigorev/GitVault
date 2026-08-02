using System.Runtime.InteropServices;
using GitVault.Core.Abstractions;
using GitVault.Core.Platform;
using GitVault.Core.Ssh;
using GitVault.Core.Ssh.Agent;
using GitVault.Platform.Linux;
using GitVault.Platform.MacOS;
using GitVault.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace GitVault.App.Composition;

/// <summary>
/// The single place in the application that branches on the operating system. Everything
/// downstream depends only on the interfaces registered here.
/// </summary>
internal static class PlatformModule
{
    /// <summary>Registers the platform-specific service implementations for the current OS.</summary>
    /// <param name="services">Service collection to add to.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<PlatformPathsBase, WindowsPlatformPaths>();
            services.AddSingleton<IPlatformInfo, WindowsPlatformInfo>();
            services.AddSingleton<IShellLauncher, WindowsShellLauncher>();
            services.AddSingleton<IGitInstallHints, WindowsGitInstallHints>();
            services.AddSingleton<ISshToolHints, WindowsSshToolHints>();
            services.AddSingleton<IFilePermissionService, WindowsFilePermissionService>();
            services.AddSingleton<IAgentEndpointProvider, WindowsAgentEndpointProvider>();
            services.AddSingleton<ISshAgentTransportFactory, WindowsAgentTransportFactory>();
            services.AddSingleton<ICredentialVault, WindowsCredentialManagerVault>();
            services.AddSingleton<ICredentialVault, GcmDpapiStoreVault>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<PlatformPathsBase, MacPlatformPaths>();
            services.AddSingleton<IPlatformInfo, MacPlatformInfo>();
            services.AddSingleton<IShellLauncher, MacShellLauncher>();
            services.AddSingleton<IGitInstallHints, MacGitInstallHints>();
            services.AddSingleton<ISshToolHints, MacSshToolHints>();
            services.AddSingleton<IFilePermissionService, MacFilePermissionService>();
            services.AddSingleton<IAgentEndpointProvider, MacAgentEndpointProvider>();
            services.AddSingleton<ICredentialVault, MacKeychainVault>();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<PlatformPathsBase, LinuxPlatformPaths>();
            services.AddSingleton<IPlatformInfo, LinuxPlatformInfo>();
            services.AddSingleton<IShellLauncher, LinuxShellLauncher>();
            services.AddSingleton<IGitInstallHints, LinuxGitInstallHints>();
            services.AddSingleton<ISshToolHints, LinuxSshToolHints>();
            services.AddSingleton<IFilePermissionService, LinuxFilePermissionService>();
            services.AddSingleton<IAgentEndpointProvider, LinuxAgentEndpointProvider>();
            services.AddSingleton<ICredentialVault, SecretServiceVault>();
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
