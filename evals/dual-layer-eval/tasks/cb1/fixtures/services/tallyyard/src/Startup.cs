using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Tallyyard;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (flags.IsOn("tallyyard.bulk_mode"))
        {
            o.CircuitThreshold = Math.Min(o.CircuitThreshold, 50);
        }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9136;
        if (env.IsProduction)
        {
            a.MaxConnections = 4;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "tallyyard";
        t.Tags["tier"] = "silver";
    }
}
