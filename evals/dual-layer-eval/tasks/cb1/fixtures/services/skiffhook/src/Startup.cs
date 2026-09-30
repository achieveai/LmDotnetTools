using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Skiffhook;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (env.Name == "prod")
        {
            o.QueueDepth *= 2;
        }

        if (flags.IsOn("skiffhook.wide_window"))
        {
            o.MaxConnections += 16;
        }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9168;
        if (env.IsProduction)
        {
            a.MaxConnections = 6;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "skiffhook";
        t.Tags["tier"] = "gold";
    }
}
