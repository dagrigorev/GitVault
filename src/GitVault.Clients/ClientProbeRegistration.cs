using GitVault.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace GitVault.Clients;

/// <summary>Registers every client probe.</summary>
public static class ClientProbeRegistration
{
    /// <summary>
    /// Adds the code-backed probes and one probe per embedded manifest.
    /// </summary>
    /// <remarks>
    /// Probes are discovered by reflection over this assembly rather than listed by hand, so a
    /// new probe becomes active by existing. Anything purely path-based should be a manifest
    /// instead, which needs no code at all.
    /// </remarks>
    /// <param name="services">Service collection to add to.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddClientProbes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IClientEnvironment, ClientEnvironment>();

        var probeTypes = typeof(ClientProbeRegistration).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true }
                        && typeof(IClientProbe).IsAssignableFrom(t)

                        // Manifest probes need a manifest, so they are constructed below.
                        && t != typeof(ManifestClientProbe))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

        foreach (var type in probeTypes)
        {
            services.AddSingleton(typeof(IProbe), type);
        }

        foreach (var manifest in ManifestClientProbe.LoadEmbeddedManifests())
        {
            var captured = manifest;
            services.AddSingleton<IProbe>(sp =>
                new ManifestClientProbe(sp.GetRequiredService<IClientEnvironment>(), captured));
        }

        return services;
    }
}
