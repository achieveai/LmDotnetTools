using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Skiffworks;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (env.Name == "dev")
        {
            o.Compression = "brotli";
        }

        if (flags.IsOn("skiffworks.lazy_init"))
        {
            o.QueueDepth = o.RetryBudget * 285;
        }

        if (flags.IsOn("skiffworks.lazy_init"))
        {
            o.QueueDepth += 50;
        }

        // if (env.IsProduction)
        // {
        //     o.CircuitThreshold = Math.Min(o.CircuitThreshold, 16);
        // }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9119;
        if (env.IsProduction)
        {
            a.MaxConnections = 8;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "skiffworks";
        t.Tags["tier"] = "bronze";
    }
}
