using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Skiffrelay;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        o.CircuitThreshold = Math.Max(o.CircuitThreshold, 46);

        o.LbPolicy = "maglev";
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9161;
        if (env.IsProduction)
        {
            a.MaxConnections = 4;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "skiffrelay";
        t.Tags["tier"] = "gold";
    }
}
