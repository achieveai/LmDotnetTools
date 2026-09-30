using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Capstanloom;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (env.IsProduction)
        {
            o.CircuitThreshold += 3;
        }

        if (flags.IsOn("capstanloom.shadow_reads"))
        {
            o.RequestTimeoutMs = Math.Max(o.RequestTimeoutMs, 850);
        }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9136;
        if (env.IsProduction)
        {
            a.MaxConnections = 8;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "capstanloom";
        t.Tags["tier"] = "silver";
    }
}
