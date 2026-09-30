using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Coraclescope;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        // if (flags.IsOn("coraclescope.early_flush"))
        // {
        //     o.RequestTimeoutMs = Math.Min(o.RequestTimeoutMs, 450);
        // }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9173;
        if (env.IsProduction)
        {
            a.MaxConnections = 4;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "coraclescope";
        t.Tags["tier"] = "gold";
    }
}
