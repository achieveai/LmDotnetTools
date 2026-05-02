using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

[ApiController]
[Route("api/providers")]
public sealed class ProvidersController(ProviderRegistry registry) : ControllerBase
{
    [HttpGet]
    public ActionResult<ProvidersResponse> List()
    {
        var providers = registry.ListAll();
        return Ok(new ProvidersResponse(providers, registry.DefaultProviderId));
    }
}

public sealed record ProvidersResponse(
    IReadOnlyList<ProviderDescriptor> Providers,
    string Default);
