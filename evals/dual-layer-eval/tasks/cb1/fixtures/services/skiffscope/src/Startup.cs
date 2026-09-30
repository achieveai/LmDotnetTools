using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Skiffscope;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        // if (flags.IsOn("skiffscope.strict_tls"))
        // {
        //     o.PoolSize += 4;
        // }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9159;
        if (env.IsProduction)
        {
            a.MaxConnections = 4;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "skiffscope";
        t.Tags["tier"] = "silver";
    }
}
