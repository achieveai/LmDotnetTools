using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Parcelscope;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        if (o.Compression == "gzip")
        {
            o.Compression = "zstd";
        }

        if (flags.IsOn("parcelscope.quiet_retries"))
        {
            o.Compression = "none";
        }

        // if (env.IsProduction)
        // {
        //     o.Compression = "none";
        // }

        if (env.IsProduction)
        {
            o.CircuitThreshold = Math.Min(o.CircuitThreshold, 51);
        }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9107;
        if (env.IsProduction)
        {
            a.MaxConnections = 6;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "parcelscope";
        t.Tags["tier"] = "gold";
    }
}
