using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// Wires the service-to-service lifecycle runtime into a host (ADR 0005).
/// </summary>
/// <remarks>
/// <para>
/// <b>No service is registered implicitly.</b> Referencing this assembly registers nothing; a host
/// opts in by calling these methods, and each one is a no-op unless its own configuration section
/// says <c>Enabled: true</c>. With the flags off the delivery pipeline is not constructed and the
/// approval gate is not registered.
/// </para>
/// <para>
/// <b>Routes are the exception, and they need the opposite handling.</b> MVC discovers controllers
/// from the entry assembly and from every assembly the entry assembly names in an
/// <see cref="ApplicationPartAttribute"/> — and the .NET SDK generates one of those for each
/// referenced assembly that itself references MVC, which this one does. So the control-plane
/// endpoints are already published on any host that merely takes a dependency here, and with the
/// flags off they are published <i>and unconstructible</i>, because their dependencies were never
/// registered. <see cref="AddLifecycleControlPlane(IMvcBuilder, IConfiguration)"/> is what removes
/// them, and a host should call it whether or not it intends to turn the feature on. An absent route
/// cannot be probed; a 500 tells a prober the feature is here and misconfigured.
/// </para>
/// </remarks>
public static class LifecycleHostingExtensions
{
    /// <summary>
    /// Name of the <see cref="IHttpClientFactory"/> client lifecycle deliveries are sent on.
    /// </summary>
    public const string DeliveryHttpClientName = "lifecycle-delivery";

    /// <summary>
    /// Registers the lifecycle delivery runtime — subscription registry, redactor, signed HTTP
    /// sender, and the fan-out pipeline — when <c>Lifecycle:Delivery:Enabled</c> is set.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">Configuration root holding
    /// <see cref="LifecycleDeliveryOptions.SectionName"/>.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The pipeline is registered once and surfaced three ways — as itself, as
    /// <see cref="ILifecyclePublisher"/>, and as an <see cref="IHostedService"/> — because they must
    /// be the same object: the hosted-service contract is what gives the drain on shutdown a live
    /// network to drain onto, and a second instance would drain an empty queue while the real one
    /// was abandoned.
    /// </para>
    /// <para>
    /// <see cref="ILifecycleOwnerResolver"/> is registered with <c>TryAdd</c>, defaulting to
    /// <see cref="SandboxLifecycleOwnerResolver"/> — which requires the host to have registered a
    /// <see cref="SandboxSessionRegistry"/>, though it does not resolve one until the first event
    /// needs an owner. A host that scopes ownership some other way registers its own resolver and
    /// this one steps aside.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The configured options are not internally
    /// consistent; see <see cref="LifecycleDeliveryOptions.Validate"/>.</exception>
    public static IServiceCollection AddLifecycleDelivery(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReadDeliveryOptions(configuration);
        if (!options.Enabled)
        {
            return services;
        }

        // Validated at wiring time, not at the first delivery: a bad retry budget discovered an hour
        // into a run is a bad retry budget nobody connects to the deployment that introduced it.
        options.Validate();

        // The instance, not the type, so every consumer — pipeline, gate, publisher, registry,
        // controllers — decides against the same validated object. A second binding could disagree
        // with this one about the allow-list, and the whole point of re-checking egress at three
        // moments is that all three answer identically.
        _ = services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LifecycleContentRedactor>();
        services.TryAddSingleton<ILifecycleSubscriptionRegistry, InMemoryLifecycleSubscriptionRegistry>();

        // Deferred, not eager, and the host will not boot otherwise. A host registers
        // SandboxSessionRegistry so the registry can publish its own lifecycle events, so the registry
        // depends on the publisher — which is the pipeline below, which depends on this resolver. An
        // eager `SandboxSessionRegistry` constructor argument closes that loop inside the container,
        // and a container cycle behind factory delegates is not reported: the host simply never
        // finishes starting.
        services.TryAddSingleton<ILifecycleOwnerResolver>(sp => new SandboxLifecycleOwnerResolver(() =>
            sp.GetRequiredService<SandboxSessionRegistry>()
        ));

        _ = services
            .AddHttpClient(DeliveryHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    // Redirects are refused rather than followed. A 302 from an allow-listed callback
                    // would otherwise re-POST the signed body — conversation content, or a tool's
                    // arguments — to whatever host the response named, which is precisely the host
                    // the allow-list did not admit. The sender classifies a 3xx as a permanent
                    // rejection, and that classification is only true if nothing chased it first.
                    AllowAutoRedirect = false,

                    // Bounds how long a connection admitted by ConnectCallback stays poolable.
                    // Without it a single connection vetted before a DNS change could carry
                    // deliveries to the old address for the life of the process, which would make
                    // "validated on every connection attempt" true and beside the point.
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),

                    // The allow-list authorizes a name; this authorizes the address behind it, at the
                    // only moment the address is knowable. Same options instance as every other
                    // check, so narrowing the configuration narrows this too.
                    ConnectCallback = (context, cancellationToken) =>
                        ConnectToValidatedAddressAsync(context, options, cancellationToken),
                }
            );

        services.TryAddSingleton<ILifecycleDeliverySender>(sp =>
            HttpLifecycleDeliverySender.OverSharedClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(DeliveryHttpClientName),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<HttpLifecycleDeliverySender>>()
            )
        );

        services.TryAddSingleton<LifecycleDeliveryPipeline>();
        services.TryAddSingleton<ILifecyclePublisher>(sp => sp.GetRequiredService<LifecycleDeliveryPipeline>());
        _ = services.AddHostedService(sp => sp.GetRequiredService<LifecycleDeliveryPipeline>());

        return AddLifecycleBundle(services);
    }

    /// <summary>
    /// Registers remote tool approval — the pending-decision store, the callback publisher, and the
    /// gate itself — when <c>Lifecycle:Approval:Enabled</c> is set.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">Configuration root holding
    /// <see cref="RemoteApprovalOptions.SectionName"/>.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// Enabling this makes every host tool call in every multi-turn loop wait for a remote decision,
    /// and block when no approver answers. That is the intended meaning of the flag, and it is worth
    /// stating plainly: this is not an observability feature with a gate attached, it is a gate.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Approval is enabled while lifecycle delivery is not. The gate needs the subscription registry
    /// to find an approver and the delivery transport to reach one, so that combination is not a
    /// stricter deployment — it is one where every tool call blocks until it expires, for a reason
    /// nothing reports.
    /// </exception>
    public static IServiceCollection AddRemoteToolApproval(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReadApprovalOptions(configuration);
        if (!options.Enabled)
        {
            return services;
        }

        // Checked against configuration rather than against what is already in the container, so the
        // answer does not depend on the order the host called these two methods in.
        if (!ReadDeliveryOptions(configuration).Enabled)
        {
            throw new InvalidOperationException(
                $"{RemoteApprovalOptions.SectionName}:Enabled is set while "
                    + $"{LifecycleDeliveryOptions.SectionName}:Enabled is not. Remote approval reaches "
                    + "approvers through the lifecycle delivery runtime, so with delivery off every "
                    + "gated tool call would block until its approval expired."
            );
        }

        options.Validate();

        _ = services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RemoteApprovalStore>();
        services.TryAddSingleton<IToolApprovalRequestPublisher, LifecycleApprovalRequestPublisher>();

        // Enumerable, because ToolApprovalOptions.Gates is a list every member of which must allow a
        // call. A host that adds its own gate keeps it; this one joins rather than replaces.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolApprovalGate, RemoteToolApprovalGate>());

        return AddLifecycleBundle(services);
    }

    /// <summary>
    /// Exposes the lifecycle control-plane endpoints — subscription management and approval
    /// decisions — on a host that has already called <see cref="AddLifecycleDelivery"/> and,
    /// optionally, <see cref="AddRemoteToolApproval"/>.
    /// </summary>
    /// <param name="builder">The result of <c>AddControllers()</c>.</param>
    /// <param name="configuration">The same configuration the service registrations were read from.
    /// Which controllers are admitted follows the same two flags, so a feature that is off has no
    /// route rather than a route that refuses.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <b>Call this even when both flags are off.</b> The .NET SDK writes an
    /// <see cref="ApplicationPartAttribute"/> for every referenced assembly that references MVC, so a
    /// host that merely takes a dependency on this one already has its controllers discovered — the
    /// lifecycle ones included, and those cannot be constructed unless their options are registered.
    /// The disabled case is therefore the one that most needs this method: it is what turns
    /// "published and broken" into "absent".
    /// </para>
    /// <para>
    /// <b>This assembly holds controllers that are not part of the lifecycle surface</b> —
    /// <c>api/auth/webhook</c> and <c>api/auth/egress-keys</c> among them. Whether those stay
    /// published depends on who put the assembly in the part list, which is the only available signal
    /// for whether the host wanted them: a host the SDK wired up is already serving them and must not
    /// have them taken away, while a host that reaches this assembly only through this method asked
    /// for lifecycle and gets lifecycle. Controllers from every other assembly — the host's own
    /// included — are decided exactly as the default provider would decide them.
    /// </para>
    /// <para>
    /// Narrowing requires <i>removing</i> the default provider, not merely adding to it: MVC unions
    /// what its feature providers return, so an allow-list sitting beside the default allow-list is
    /// not an allow-list at all. For the same reason a host that has installed a controller feature
    /// provider of its own — any <see cref="IApplicationFeatureProvider{ControllerFeature}"/>, not
    /// just a <see cref="ControllerFeatureProvider"/> subclass — is refused rather than silently
    /// unioned with: two allow-lists cannot both be authoritative, and the union is always the wider
    /// one. Such a host should name the lifecycle controllers in its own provider instead of calling
    /// this method. A provider <i>this</i> method installed is not foreign, so calling it twice is
    /// safe and the second call simply re-states the first.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A flag is set but the matching services were never registered on <see cref="IMvcBuilder.Services"/>,
    /// or the host has a controller feature provider of its own; see the remarks.
    /// </exception>
    public static IMvcBuilder AddLifecycleControlPlane(this IMvcBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var admitted = new HashSet<Type>();
        if (ReadDeliveryOptions(configuration).Enabled)
        {
            RequireRegistered<LifecycleDeliveryOptions>(
                builder.Services,
                LifecycleDeliveryOptions.SectionName,
                nameof(AddLifecycleDelivery)
            );
            _ = admitted.Add(typeof(LifecycleSubscriptionsController));
        }

        if (ReadApprovalOptions(configuration).Enabled)
        {
            RequireRegistered<RemoteApprovalOptions>(
                builder.Services,
                RemoteApprovalOptions.SectionName,
                nameof(AddRemoteToolApproval)
            );
            _ = admitted.Add(typeof(LifecycleApprovalController));
        }

        return builder.ConfigureApplicationPartManager(parts =>
        {
            var assembly = typeof(LifecycleSubscriptionsController).Assembly;

            // What an earlier call to this method left behind, so a repeat call recognizes its own
            // work instead of reporting it as a host provider it must not overrule.
            var installed = parts.FeatureProviders.OfType<LifecycleControllerFeatureProvider>().ToList();

            var foreign = parts
                .FeatureProviders.OfType<IApplicationFeatureProvider<ControllerFeature>>()
                .Where(provider =>
                    provider.GetType() != typeof(ControllerFeatureProvider)
                    && provider is not LifecycleControllerFeatureProvider
                )
                .ToList();
            if (foreign.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The host has already installed a controller feature provider "
                        + $"({string.Join(", ", foreign.Select(p => p.GetType().FullName))}). MVC unions "
                        + "what its feature providers return, so this method cannot narrow discovery "
                        + "without silently widening that one. Name "
                        + $"{nameof(LifecycleSubscriptionsController)} and "
                        + $"{nameof(LifecycleApprovalController)} in the existing provider instead of "
                        + $"calling {nameof(AddLifecycleControlPlane)}."
                );
            }

            // The part is added at most once across every call, because a second AssemblyPart for the
            // same assembly discovers every controller in it twice and turns each route ambiguous.
            var addedPart = installed.Select(provider => provider.AddedPart).FirstOrDefault();
            if (
                admitted.Count > 0
                && !parts.ApplicationParts.OfType<AssemblyPart>().Any(part => part.Assembly == assembly)
            )
            {
                addedPart = new AssemblyPart(assembly);
                parts.ApplicationParts.Add(addedPart);
            }

            foreach (
                var existing in parts.FeatureProviders.OfType<IApplicationFeatureProvider<ControllerFeature>>().ToList()
            )
            {
                _ = parts.FeatureProviders.Remove(existing);
            }

            parts.FeatureProviders.Add(
                new LifecycleControllerFeatureProvider(
                    admitted,
                    addedPart,
                    // Deferred, and that is the whole point: ConfigureApplicationPartManager runs this
                    // callback immediately, so anything read here is read before the host has finished
                    // composing MVC. A host that calls AddApplicationPart for this assembly afterwards
                    // — to publish api/auth/* — would have been judged as though it never had, and its
                    // own endpoints would vanish.
                    () => parts.ApplicationParts
                )
            );
        });
    }

    /// <summary>
    /// Refuses to publish a route whose controller could not be constructed.
    /// </summary>
    /// <remarks>
    /// The flag being on says the host wants the feature; <typeparamref name="TOptions"/> being in the
    /// container says the host actually wired it. Without this the two can disagree silently and the
    /// disagreement surfaces as a 500 from a published endpoint — which is the outcome this whole
    /// method exists to avoid, arrived at from the other direction. A route that is merely absent is
    /// also the safer failure: it tells a prober nothing.
    /// </remarks>
    private static void RequireRegistered<TOptions>(IServiceCollection services, string section, string registrar)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(TOptions)))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{section}:Enabled is set, so {nameof(AddLifecycleControlPlane)} would publish the "
                + $"matching route, but {nameof(LifecycleHostingExtensions)}.{registrar} has not run on "
                + "this service collection — the controller behind that route has no dependencies to "
                + $"construct and would answer 500. Call services.{registrar}(configuration) before "
                + $"{nameof(AddLifecycleControlPlane)}."
        );
    }

    /// <summary>
    /// Registers the bundle a multi-turn loop reads its lifecycle wiring from, if nothing else has.
    /// </summary>
    /// <remarks>
    /// One bundle for the process, because <see cref="MultiTurnLifecycleServices.SequenceAllocator"/>
    /// owns the producer epoch: loops that share a bundle share an epoch, which is what lets a
    /// subscriber tell "the producer restarted" from "events were lost". Two bundles would interleave
    /// two epochs and read as a restart on every conversation.
    /// </remarks>
    private static IServiceCollection AddLifecycleBundle(IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            // Resolved lazily inside the factory, so this is correct no matter which order the host
            // called AddLifecycleDelivery and AddRemoteToolApproval in.
            var gates = sp.GetServices<IToolApprovalGate>().ToArray();
            return new MultiTurnLifecycleServices
            {
                Publisher = sp.GetService<ILifecyclePublisher>() ?? NullLifecyclePublisher.Instance,
                TimeProvider = sp.GetRequiredService<TimeProvider>(),
                Approval =
                    gates.Length == 0
                        ? ToolInvocationPreparer.Disabled
                        : new ToolInvocationPreparer(
                            new ToolApprovalOptions { Gates = gates },
                            sp.GetRequiredService<ILogger<ToolInvocationPreparer>>()
                        ),
            };
        });

        return services;
    }

    /// <summary>
    /// Resolves a callback host and connects only to an address the egress policy admits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the half of the destination check that the allow-list cannot do. The allow-list
    /// authorizes a <i>name</i>; the name-to-address mapping belongs to whoever controls that name's
    /// DNS, which for a subscriber-supplied host is the subscriber. Resolving here — on every
    /// connection attempt, not once at registration — is what stops an allow-listed name from being
    /// repointed at the host's own loopback or at a metadata endpoint between registration and
    /// delivery.
    /// </para>
    /// <para>
    /// The socket is dialled against the <i>vetted addresses</i> rather than the host name.
    /// Connecting by name would re-resolve inside the socket and reopen the exact gap between check
    /// and use that this callback exists to close.
    /// </para>
    /// </remarks>
    private static async ValueTask<Stream> ConnectToValidatedAddressAsync(
        SocketsHttpConnectionContext context,
        LifecycleDeliveryOptions options,
        CancellationToken cancellationToken
    )
    {
        var endPoint = context.DnsEndPoint;
        var resolved = await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken).ConfigureAwait(false);

        var permitted = Array.FindAll(
            resolved,
            address => LifecycleDestinationPolicy.IsAllowedAddress(address, options)
        );

        if (permitted.Length == 0)
        {
            // Deliberately says nothing about what the host resolved to. The sender logs this and the
            // subscriber never sees it, but an operator reading a log should not have to wonder
            // whether the exception text itself became a way to probe internal addressing.
            throw new HttpRequestException(
                $"'{endPoint.Host}' resolved to no address a lifecycle callback may reach. "
                    + $"Set {LifecycleDeliveryOptions.SectionName}:"
                    + $"{nameof(LifecycleDeliveryOptions.AllowPrivateCallbackAddresses)} only if the "
                    + "subscriber is genuinely on this machine or private network."
            );
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(permitted, endPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static LifecycleDeliveryOptions ReadDeliveryOptions(IConfiguration configuration) =>
        configuration.GetSection(LifecycleDeliveryOptions.SectionName).Get<LifecycleDeliveryOptions>()
        ?? new LifecycleDeliveryOptions();

    private static RemoteApprovalOptions ReadApprovalOptions(IConfiguration configuration) =>
        configuration.GetSection(RemoteApprovalOptions.SectionName).Get<RemoteApprovalOptions>()
        ?? new RemoteApprovalOptions();
}

/// <summary>
/// Restricts MVC's controller discovery so that this assembly exposes the enabled lifecycle
/// endpoints, keeps whatever else of it the host was already serving, and adds nothing new.
/// </summary>
/// <param name="admitted">The lifecycle controller types this host has enabled.</param>
/// <param name="addedPart">
/// The application part <see cref="LifecycleHostingExtensions.AddLifecycleControlPlane"/> added for
/// this assembly, or <c>null</c> when it added none. Held so that the one part this SDK contributed
/// can be told apart from a part the host contributed for the same assembly.
/// </param>
/// <param name="applicationParts">
/// Reads the part list as it stands when discovery runs. A function rather than a value because
/// <c>ConfigureApplicationPartManager</c> invokes its callback immediately, so a value read there
/// would be read before the host has finished composing MVC.
/// </param>
internal sealed class LifecycleControllerFeatureProvider(
    IReadOnlySet<Type> admitted,
    ApplicationPart? addedPart,
    Func<IEnumerable<ApplicationPart>> applicationParts
) : ControllerFeatureProvider
{
    private static readonly Assembly LifecycleAssembly = typeof(LifecycleControllerFeatureProvider).Assembly;

    /// <summary>
    /// The controllers whose visibility this method owns. Listed by type rather than by namespace so
    /// that a controller added to this assembly later is not silently swept into the lifecycle
    /// feature's opt-in — it keeps whatever visibility the rest of the assembly has.
    /// </summary>
    private static readonly HashSet<Type> Gated =
    [
        typeof(LifecycleSubscriptionsController),
        typeof(LifecycleApprovalController),
    ];

    /// <summary>
    /// Whether this assembly is an application part for a reason other than this SDK making it one —
    /// normally because the SDK wrote an <see cref="ApplicationPartAttribute"/> for it, or the host
    /// called <c>AddApplicationPart</c>. When it is, this assembly's non-lifecycle controllers are the
    /// host's and stay; when it is not, they were never on offer and do not start being so now.
    /// </summary>
    /// <remarks>
    /// Answered once, on the first discovery, rather than at registration: the host may add parts
    /// after <see cref="LifecycleHostingExtensions.AddLifecycleControlPlane"/> returns, and an answer
    /// snapshotted then would drop endpoints the host went on to publish. Deferring it to the first
    /// <see cref="IsController"/> call reads the list the host actually ended up with.
    /// </remarks>
    private readonly Lazy<bool> _hostSuppliedThePart = new(() =>
        applicationParts()
            .OfType<AssemblyPart>()
            .Any(part => part.Assembly == LifecycleAssembly && !ReferenceEquals(part, addedPart))
    );

    /// <summary>The part this SDK added, so a repeat registration reuses it instead of adding a second.</summary>
    internal ApplicationPart? AddedPart => addedPart;

    /// <inheritdoc />
    protected override bool IsController(TypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        if (!base.IsController(typeInfo))
        {
            return false;
        }

        var type = typeInfo.AsType();
        if (Gated.Contains(type))
        {
            return admitted.Contains(type);
        }

        // Everything else keeps the answer it would have had. This provider replaced the default one —
        // which MVC needed in order for the gate above to mean anything — so it is now also
        // responsible for the host's own controllers, and quietly dropping them would turn "expose the
        // lifecycle endpoints" into "expose only the lifecycle endpoints".
        return type.Assembly != LifecycleAssembly || _hostSuppliedThePart.Value;
    }
}
