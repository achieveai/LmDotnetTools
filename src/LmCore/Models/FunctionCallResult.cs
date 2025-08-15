using System.Text.Json;
using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmCore.Models;

public abstract record ContentBlock {
    public required string Type { get; init; }
}

public record TextBlock : ContentBlock {
    public required string Text { get; init; }
}


public abstract record FunctionCallResult {
    public bool? IsError { get; init; }

    public IList<ContentBlock> Content { get; init; } = new List<ContentBlock>();

    public abstract JsonNode ToStructuredJson();
}

public record FunctionCallResult<T> : FunctionCallResult {
    public required T Result { get; init; }

    public override JsonNode ToStructuredJson() => JsonSerializer.SerializeToNode(this.Result)!;
}