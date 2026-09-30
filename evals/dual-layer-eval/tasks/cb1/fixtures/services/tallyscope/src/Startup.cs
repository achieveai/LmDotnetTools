using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Tallyscope;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        // if (flags.IsOn("tallyscope.wide_window"))
        // {
        //     o.MaxConnections = Math.Min(o.MaxConnections, 72);
        // }

        if (env.IsProduction)
        {
            o.RequestTimeoutMs += 100;
        }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9105;
        if (env.IsProduction)
        {
            a.MaxConnections = 6;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "tallyscope";
        t.Tags["tier"] = "bronze";
    }
}
