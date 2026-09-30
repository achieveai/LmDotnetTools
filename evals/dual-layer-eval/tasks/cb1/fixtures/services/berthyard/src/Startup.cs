using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Berthyard;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (flags.IsOn("berthyard.lazy_init"))
        {
            o.LbPolicy = "random";
        }

        if (flags.IsOn("berthyard.lazy_init"))
        {
            o.CacheTtlSeconds += 30;
        }

        if (!env.IsProduction)
        {
            o.CacheTtlSeconds += 180;
        }

        o.CacheTtlSeconds = Math.Max(o.CacheTtlSeconds, 2400);
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9122;
        if (env.IsProduction)
        {
            a.MaxConnections = 6;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "berthyard";
        t.Tags["tier"] = "bronze";
    }
}
