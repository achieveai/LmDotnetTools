using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Coraclegate;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        o.MaxConnections *= 2;
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9106;
        if (env.IsProduction)
        {
            a.MaxConnections = 8;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "coraclegate";
        t.Tags["tier"] = "bronze";
    }
}
