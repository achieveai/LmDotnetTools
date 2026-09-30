namespace Quillfeather.Platform;

public static class MeshHost
{
    /// <summary>
    /// Start-up order: merge the YAML layers (ConfigLoader), bind the mesh section to MeshOptions, load
    /// feature flags, then call the service's Startup.ConfigureMesh, which has the last word.
    /// </summary>
    public static MeshOptions Build(
        ConfigLoader loader,
        IOptionsBinder binder,
        IFeatureFlags flags,
        MeshEnvironment env,
        IServiceStartup startup,
        string service
    )
    {
        var options = binder.BindMesh(loader.LayersFor(service, env.Name));
        startup.ConfigureMesh(options, env, flags);
        return options;
    }
}
