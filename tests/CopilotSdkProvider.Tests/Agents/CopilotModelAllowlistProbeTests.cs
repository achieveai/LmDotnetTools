using System.Reflection;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Agents;

/// <summary>
/// Exercises the fail-fast model allowlist probe on <see cref="CopilotSdkClient"/>.
/// The probe runs immediately after <c>initialize</c> and throws when the requested
/// model is not present in the server-advertised list. When the server does not
/// publish a model list, the probe is a no-op.
/// </summary>
public class CopilotModelAllowlistProbeTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static CopilotSdkClient NewClient()
    {
        return new CopilotSdkClient(new CopilotSdkOptions { Model = "gpt-5" });
    }

    private static Action InvokeEnsureModelAllowed(
        CopilotSdkClient client,
        JsonElement initializeResponse,
        string? requestedModel)
    {
        var method = typeof(CopilotSdkClient).GetMethod(
            "EnsureModelAllowed",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EnsureModelAllowed not found");

        return () =>
        {
            try
            {
                _ = method.Invoke(client, [initializeResponse, requestedModel]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        };
    }

    [Fact]
    public void EnsureModelAllowed_ModelInList_Passes()
    {
        var client = NewClient();
        var response = Parse("""{"models": ["gpt-5", "gpt-4o"]}""");

        InvokeEnsureModelAllowed(client, response, "gpt-5").Should().NotThrow();
    }

    [Fact]
    public void EnsureModelAllowed_ModelNotInList_Throws()
    {
        var client = NewClient();
        var response = Parse("""{"models": ["gpt-4o"]}""");

        InvokeEnsureModelAllowed(client, response, "gpt-5")
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*gpt-5*gpt-4o*");
    }

    [Fact]
    public void EnsureModelAllowed_EmptyModelList_IsNoop()
    {
        var client = NewClient();
        var response = Parse("""{"models": []}""");

        // Empty advertised list means the server does not enforce; probe should not throw.
        InvokeEnsureModelAllowed(client, response, "gpt-5").Should().NotThrow();
    }

    [Fact]
    public void EnsureModelAllowed_NoModelListField_IsNoop()
    {
        var client = NewClient();
        var response = Parse("""{"other": true}""");

        InvokeEnsureModelAllowed(client, response, "gpt-5").Should().NotThrow();
    }

    [Fact]
    public void EnsureModelAllowed_NullOrEmptyRequestedModel_IsNoop()
    {
        var client = NewClient();
        var response = Parse("""{"models": ["gpt-4o"]}""");

        InvokeEnsureModelAllowed(client, response, null).Should().NotThrow();
        InvokeEnsureModelAllowed(client, response, "").Should().NotThrow();
    }

    [Fact]
    public void EnsureModelAllowed_SupportedModelsAlternateField_HonoredWhenModelsMissing()
    {
        var client = NewClient();
        var response = Parse("""{"supportedModels": ["gpt-5"]}""");

        InvokeEnsureModelAllowed(client, response, "gpt-5").Should().NotThrow();
        InvokeEnsureModelAllowed(client, response, "claude-3-opus")
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EnsureModelAllowed_AvailableModelsAlternateField_HonoredWhenOthersMissing()
    {
        var client = NewClient();
        var response = Parse("""{"availableModels": ["gpt-5"]}""");

        InvokeEnsureModelAllowed(client, response, "gpt-5").Should().NotThrow();
    }

    [Fact]
    public void EnsureModelAllowed_ObjectsWithIdFieldInArray_Extracted()
    {
        var client = NewClient();
        var response = Parse("""{"models": [{"id": "gpt-5"}, {"id": "gpt-4o"}]}""");

        InvokeEnsureModelAllowed(client, response, "gpt-5").Should().NotThrow();
        InvokeEnsureModelAllowed(client, response, "not-there")
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EnsureModelAllowed_CaseInsensitiveComparison()
    {
        var client = NewClient();
        var response = Parse("""{"models": ["GPT-5"]}""");

        InvokeEnsureModelAllowed(client, response, "gpt-5").Should().NotThrow();
    }
}
