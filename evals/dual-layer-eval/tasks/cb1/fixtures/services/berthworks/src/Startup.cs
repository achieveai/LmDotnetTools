using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Berthworks;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (flags.IsOn("berthworks.quiet_retries"))
        {
            o.LbPolicy = "maglev";
        }

        o.CacheTtlSeconds = Math.Max(o.CacheTtlSeconds, 2880);
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9135;
        if (env.IsProduction)
        {
            a.MaxConnections = 6;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "berthworks";
        t.Tags["tier"] = "gold";
    }
}
