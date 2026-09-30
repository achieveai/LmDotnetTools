using System;
using Quillfeather.Platform;

namespace Quillfeather.Services.Manifestgate;

public static class Startup
{
    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)
    {
        // Runs after every configuration layer has been merged (see docs/CONFIG.md).

        o.RequestTimeoutMs = Math.Max(o.RequestTimeoutMs, 2250);

        if (flags.IsOn("manifestgate.strict_tls"))
        {
            o.LbPolicy = "maglev";
        }
    }

    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)
    {
        a.Port = 9146;
        if (env.IsProduction)
        {
            a.MaxConnections = 4;
        }
    }

    public static void ConfigureTelemetry(TelemetryOptions t)
    {
        t.ServiceName = "manifestgate";
        t.Tags["tier"] = "silver";
    }
}
