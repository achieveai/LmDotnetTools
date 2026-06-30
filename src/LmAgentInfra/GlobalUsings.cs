// These namespaces are provided implicitly by the ASP.NET Core Web SDK (Microsoft.NET.Sdk.Web),
// which LmStreaming.Sample used. This library uses the plain Microsoft.NET.Sdk, so we restore the
// implicit-using surface the extracted code (hosted services, controllers, logging) relies on,
// without per-file using churn.
global using Microsoft.AspNetCore.Http;
global using Microsoft.AspNetCore.Routing;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
