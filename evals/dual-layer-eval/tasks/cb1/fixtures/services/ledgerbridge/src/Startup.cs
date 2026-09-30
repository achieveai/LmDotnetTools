using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Ledgerbridge;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        // if (env.Name == "qa")
        // {
        //     o.Compression = "brotli";
        // }

        if (o.RetryBudget >= 8)
        {
            o.PoolSize = o.RetryBudget * 7;
        }

        if (env.Name == "prod")
        {
            o.PoolSize += 8;
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
        t.ServiceName = "ledgerbridge";
        t.Tags["tier"] = "bronze";
    }
}
