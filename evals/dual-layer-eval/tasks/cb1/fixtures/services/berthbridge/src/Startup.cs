using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Berthbridge;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (env.IsProduction)
        {
            o.RequestTimeoutMs *= 2;
        }

        if (env.IsProduction)
        {
            o.LbPolicy = "random";
        }

        if (flags.IsOn("berthbridge.warm_cache"))
        {
            o.LbPolicy = "maglev";
        }

        // if (env.IsProduction)
        // {
        //     o.LbPolicy = "random";
        // }

        if (o.RetryBudget >= 4)
        {
            o.LbPolicy = "random";
        }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9112;
        if (env.IsProduction)
        {
            a.MaxConnections = 6;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "berthbridge";
        t.Tags["tier"] = "gold";
    }
}
