using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Parcelrelay;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (env.Name == "prod")
        {
            o.PoolSize = Math.Min(o.PoolSize, 52);
        }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9152;
        if (env.IsProduction)
        {
            a.MaxConnections = 4;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "parcelrelay";
        t.Tags["tier"] = "silver";
    }
}
