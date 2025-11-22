using System.CommandLine;
using MemoryServer.Configuration;
using MemoryServer.Services;
using MemoryServer.Utils;
using Microsoft.Extensions.Options;

namespace MemoryServer.Tools;

/// <summary>
/// CLI command for generating JWT tokens
/// </summary>
public static class TokenGeneratorCommand
{
    /// <summary>
    /// Creates the generate-token command
    /// </summary>
    /// <returns>The configured command</returns>
    public static Command CreateCommand()
    {
        var userIdOption = new Option<string>(name: "--userId", description: "The user identifier for the token")
        {
            IsRequired = true,
        };

        var agentIdOption = new Option<string>(name: "--agentId", description: "The agent identifier for the token")
        {
            IsRequired = true,
        };

        var command = new Command("generate-token", "Generate a JWT token for authentication")
        {
            userIdOption,
            agentIdOption,
        };

        command.SetHandler(
            GenerateTokenAsync,
            userIdOption,
            agentIdOption
        );

        return command;
    }

    /// <summary>
    /// Generates a JWT token for the specified user and agent
    /// </summary>
    /// <param name="userId">The user identifier</param>
    /// <param name="agentId">The agent identifier</param>
    private static Task GenerateTokenAsync(string userId, string agentId)
    {
        try
        {
            // Load environment variables
            EnvironmentHelper.LoadEnvIfNeeded();

            // Build configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile(
                    $"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json",
                    optional: true
                )
                .AddEnvironmentVariables()
                .Build();

            // Build service provider
            var services = new ServiceCollection();

            // Configure JWT options
            _ = services.Configure<JwtOptions>(configuration.GetSection("Jwt"));

            // Add logging
            _ = services.AddLogging(builder => builder.AddConsole());

            // Add token service
            _ = services.AddScoped<ITokenService, TokenService>();

            var serviceProvider = services.BuildServiceProvider();

            // Validate JWT configuration
            var jwtOptions = serviceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;
            if (!jwtOptions.IsValid())
            {
                Console.Error.WriteLine("❌ JWT configuration is invalid. Please check your JWT settings.");
                Console.Error.WriteLine(
                    $"   - Secret: {(string.IsNullOrEmpty(jwtOptions.Secret) ? "Missing" : "Present")}"
                );
                Console.Error.WriteLine($"   - Issuer: {jwtOptions.Issuer}");
                Console.Error.WriteLine($"   - Audience: {jwtOptions.Audience}");
                Console.Error.WriteLine($"   - ExpirationMinutes: {jwtOptions.ExpirationMinutes}");
                Environment.Exit(1);
                return Task.CompletedTask;
            }

            // Generate token
            var tokenService = serviceProvider.GetRequiredService<ITokenService>();
            var token = tokenService.GenerateToken(userId, agentId);

            // Output the token
            Console.WriteLine("✅ JWT Token generated successfully!");
            Console.WriteLine();
            Console.WriteLine($"UserId: {userId}");
            Console.WriteLine($"AgentId: {agentId}");
            Console.WriteLine(
                $"Expires: {DateTime.UtcNow.AddMinutes(jwtOptions.ExpirationMinutes):yyyy-MM-dd HH:mm:ss} UTC"
            );
            Console.WriteLine();
            Console.WriteLine("Token:");
            Console.WriteLine(token);
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine($"Authorization: Bearer {token}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ Failed to generate token: {ex.Message}");
            Environment.Exit(1);
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }
}
