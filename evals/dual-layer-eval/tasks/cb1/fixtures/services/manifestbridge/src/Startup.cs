using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Manifestbridge;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (flags.IsOn("manifestbridge.strict_tls"))
        {
            o.RequestTimeoutMs *= 3;
        }

        if (!env.IsProduction)
        {
            o.RequestTimeoutMs *= 3;
        }

        if (env.IsProduction)
        {
            o.QueueDepth *= 2;
        }

        if (env.IsProduction)
        {
            o.LbPolicy = "roundrobin";
        }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9158;
        if (env.IsProduction)
        {
            a.MaxConnections = 8;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "manifestbridge";
        t.Tags["tier"] = "gold";
    }
}
