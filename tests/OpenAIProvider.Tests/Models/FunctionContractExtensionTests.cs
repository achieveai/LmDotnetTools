using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Models;

public class FunctionContractExtensionTests
{
    [Fact]
    public void ToOpenFunctionDefinition_ObjectParameter_KeepsItsAdditionalProperties()
    {
        // Shaped like ExecuteTool: `arguments` is an open object (the target tool's own arguments).
        var function = new FunctionContract
        {
            Name = "ExecuteTool",
            Description = "Runs a tool by name.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "arguments",
                    Description = "The tool's arguments.",
                    ParameterType = new JsonSchemaObject { Type = new("object"), AdditionalProperties = true },
                },
                new FunctionParameterContract
                {
                    Name = "closed",
                    Description = "An object that declares no additional properties.",
                    ParameterType = new JsonSchemaObject { Type = new("object") },
                },
            ],
        };

        var properties = function.ToOpenFunctionDefinition().Parameters.Properties!;

        Assert.True(properties["arguments"].AdditionalProperties);
        Assert.False(properties["closed"].AdditionalProperties);
    }
}
