// These namespaces are provided implicitly by the ASP.NET Core Web SDK (Microsoft.NET.Sdk.Web),
// which LmStreaming.Sample used. This library uses the plain Microsoft.NET.Sdk, so we restore the
// same implicit-using surface globally to keep the extracted code (hosted services, controllers,
// logging, configuration, DI) compiling without per-file using churn.
global using Microsoft.AspNetCore.Hosting;
global using Microsoft.AspNetCore.Http;
global using Microsoft.AspNetCore.Routing;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
