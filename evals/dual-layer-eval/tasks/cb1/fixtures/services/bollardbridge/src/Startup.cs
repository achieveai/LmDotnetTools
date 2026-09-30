using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Bollardbridge;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (flags.IsOn("bollardbridge.strict_tls"))
        {
            o.MaxConnections *= 2;
        }

        if (o.RetryBudget >= 5)
        {
            o.MaxConnections = Math.Min(o.MaxConnections, 344);
        }

        // if (env.IsProduction)
        // {
        //     o.MaxConnections *= 2;
        // }

        if (env.IsProduction)
        {
            o.LbPolicy = "roundrobin";
        }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9166;
        if (env.IsProduction)
        {
            a.MaxConnections = 4;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "bollardbridge";
        t.Tags["tier"] = "gold";
    }
}
