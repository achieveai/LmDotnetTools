using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Docketmill;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        o.LbPolicy = "ringhash";

        o.PoolSize *= 2;
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9129;
        if (env.IsProduction)
        {
            a.MaxConnections = 8;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "docketmill";
        t.Tags["tier"] = "bronze";
    }
}
