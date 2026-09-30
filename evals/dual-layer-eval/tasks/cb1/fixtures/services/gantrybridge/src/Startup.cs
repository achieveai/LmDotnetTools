using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Gantrybridge;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (env.Name == "dev")
        {
            o.CacheTtlSeconds = Math.Max(o.CacheTtlSeconds, 3030);
        }

        if (flags.IsOn("gantrybridge.strict_tls"))
        {
            o.CircuitThreshold *= 2;
        }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9174;
        if (env.IsProduction)
        {
            a.MaxConnections = 6;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "gantrybridge";
        t.Tags["tier"] = "bronze";
    }
}
