using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
namespace AchieveAi.LmDotnetTools.LmCore.Tests.Utils;

public class JsonSchemaValidatorTests
{
    private readonly JsonSchemaValidator _validator = new();

    [Fact]
    public void Validate_EmptyJson_ReturnsFalse()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "param1",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject { Type = "string" },
                },
            ],
        };

        var result = _validator.Validate("", contract);
        Assert.False(result);
    }

    [Fact]
    public void Validate_NullSchema_ReturnsFalse()
    {
        var result = _validator.Validate("{}", null!);
        Assert.False(result);
    }

    [Fact]
    public void Validate_InvalidJson_ReturnsFalse()
    {
        var contract = new FunctionContract { Name = "testFunction" };
        var result = _validator.Validate("invalid json", contract);
        Assert.False(result);
    }

    [Fact]
    public void Validate_NoParameters_ReturnsTrue()
    {
        var contract = new FunctionContract { Name = "testFunction" };
        var result = _validator.Validate("{}", contract);
        Assert.True(result);
    }

    [Fact]
    public void Validate_MissingRequiredParameter_ReturnsFalse()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "param1",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject { Type = "string" },
                },
            ],
        };

        var result = _validator.Validate("{}", contract);
        Assert.False(result);
    }

    [Fact]
    public void Validate_WrongType_ReturnsFalse()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "param1",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject { Type = "string" },
                },
            ],
        };

        var result = _validator.Validate("{\"param1\": 123}", contract);
        Assert.False(result);
    }

    [Fact]
    public void Validate_ValidSimpleTypes_ReturnsTrue()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "stringParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject { Type = "string" },
                },
                new FunctionParameterContract
                {
                    Name = "numberParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject { Type = "number" },
                },
                new FunctionParameterContract
                {
                    Name = "boolParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject { Type = "boolean" },
                },
            ],
        };

        var result = _validator.Validate(
            "{\"stringParam\": \"test\", \"numberParam\": 42, \"boolParam\": true}",
            contract
        );
        Assert.True(result);
    }

    [Fact]
    public void Validate_EnumConstraintViolated_ReturnsFalse()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "enumParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject { Type = "string", Enum = ["value1", "value2"] },
                },
            ],
        };

        var result = _validator.Validate("{\"enumParam\": \"value3\"}", contract);
        Assert.False(result);
    }

    [Fact]
    public void Validate_EnumConstraintSatisfied_ReturnsTrue()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "enumParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject { Type = "string", Enum = ["value1", "value2"] },
                },
            ],
        };

        var result = _validator.Validate("{\"enumParam\": \"value1\"}", contract);
        Assert.True(result);
    }

    [Fact]
    public void Validate_NumberRangeViolated_ReturnsFalse()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "rangeParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject
                    {
                        Type = "number",
                        Minimum = 10,
                        Maximum = 20,
                    },
                },
            ],
        };

        var result = _validator.Validate("{\"rangeParam\": 5}", contract);
        Assert.False(result);
    }

    [Fact]
    public void Validate_NumberRangeSatisfied_ReturnsTrue()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "rangeParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject
                    {
                        Type = "number",
                        Minimum = 10,
                        Maximum = 20,
                    },
                },
            ],
        };

        var result = _validator.Validate("{\"rangeParam\": 15}", contract);
        Assert.True(result);
    }

    [Fact]
    public void Validate_ArrayType_ReturnsTrue()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "arrayParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject
                    {
                        Type = "array",
                        Items = new JsonSchemaObject { Type = "string" },
                    },
                },
            ],
        };

        var result = _validator.Validate("{\"arrayParam\": [\"item1\", \"item2\"]}", contract);
        Assert.True(result);
    }

    [Fact]
    public void Validate_ArrayMinItemsViolated_ReturnsFalse()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "arrayParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject
                    {
                        Type = "array",
                        Items = new JsonSchemaObject { Type = "string" },
                        MinItems = 3,
                    },
                },
            ],
        };

        var result = _validator.Validate("{\"arrayParam\": [\"item1\", \"item2\"]}", contract);
        Assert.False(result);
    }

    [Fact]
    public void Validate_ArrayMaxItemsViolated_ReturnsFalse()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "arrayParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject
                    {
                        Type = "array",
                        Items = new JsonSchemaObject { Type = "string" },
                        MaxItems = 1,
                    },
                },
            ],
        };

        var result = _validator.Validate("{\"arrayParam\": [\"item1\", \"item2\"]}", contract);
        Assert.False(result);
    }

    [Fact]
    public void Validate_ArrayUniqueItemsViolated_ReturnsFalse()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "arrayParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject
                    {
                        Type = "array",
                        Items = new JsonSchemaObject { Type = "string" },
                        UniqueItems = true,
                    },
                },
            ],
        };

        var result = _validator.Validate("{\"arrayParam\": [\"item1\", \"item1\"]}", contract);
        Assert.False(result);
    }

    [Fact]
    public void Validate_ArrayConstraintsSatisfied_ReturnsTrue()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "arrayParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject
                    {
                        Type = "array",
                        Items = new JsonSchemaObject { Type = "string" },
                        MinItems = 2,
                        MaxItems = 3,
                        UniqueItems = true,
                    },
                },
            ],
        };

        var result = _validator.Validate("{\"arrayParam\": [\"item1\", \"item2\"]}", contract);
        Assert.True(result);
    }

    [Fact]
    public void Validate_NestedObject_ReturnsTrue()
    {
        var contract = new FunctionContract
        {
            Name = "testFunction",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "objectParam",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject
                    {
                        Type = "object",
                        Properties = new Dictionary<string, JsonSchemaObject>
                        {
                            {
                                "nested",
                                new JsonSchemaObject { Type = "string" }
                            },
                        },
                    },
                },
            ],
        };

        var result = _validator.Validate("{\"objectParam\": {\"nested\": \"value\"}}", contract);
        Assert.True(result);
    }
}
