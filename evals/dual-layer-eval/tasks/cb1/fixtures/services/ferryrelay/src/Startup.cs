using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Ferryrelay;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (flags.IsOn("ferryrelay.shadow_reads"))
        {
            o.CircuitThreshold *= 2;
        }

        if (env.Name == "prod")
        {
            o.CircuitThreshold *= 2;
        }

        if (!env.IsProduction)
        {
            o.CircuitThreshold = 48;
        }

        o.PoolSize *= 2;
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9127;
        if (env.IsProduction)
        {
            a.MaxConnections = 8;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "ferryrelay";
        t.Tags["tier"] = "gold";
    }
}
