using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Sluicehook;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (env.Name == "qa")
        {
            o.QueueDepth = Math.Min(o.QueueDepth, 1825);
        }

        if (flags.IsOn("sluicehook.wide_window"))
        {
            o.Compression = "zstd";
        }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9158;
        if (env.IsProduction)
        {
            a.MaxConnections = 6;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "sluicehook";
        t.Tags["tier"] = "silver";
    }
}
