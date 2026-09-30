using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Capstanscope;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (env.Name == "dev")
        {
            o.QueueDepth += 75;
        }

        o.QueueDepth = Math.Min(o.QueueDepth, 1625);
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9138;
        if (env.IsProduction)
        {
            a.MaxConnections = 4;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "capstanscope";
        t.Tags["tier"] = "gold";
    }
}
