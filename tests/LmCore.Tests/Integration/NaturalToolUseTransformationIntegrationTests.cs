using System.Collections.Immutable;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Integration;

/// <summary>
/// Integration tests demonstrating the full Natural Tool Use Transformation pipeline
/// </summary>
public class NaturalToolUseTransformationIntegrationTests
{
    [Fact]
    public void FullPipeline_SingleToolCall_TransformsCorrectly()
    {
        // Arrange - Create a realistic scenario with weather tool call
        var weatherToolCall = new ToolCall
        {
            FunctionName = "GetWeather",
            FunctionArgs = "{\"location\":\"San Francisco, CA\",\"unit\":\"celsius\",\"include_forecast\":true}"
        };
        var weatherResult = new ToolCallResult(
            "weather-call-1",
            "{\"temperature\":22,\"condition\":\"partly_cloudy\",\"humidity\":65,\"wind_speed\":12,\"forecast\":[{\"day\":\"today\",\"high\":24,\"low\":18},{\"day\":\"tomorrow\",\"high\":26,\"low\":19}]}"
        );

        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls = [weatherToolCall],
            Role = Role.Assistant,
            FromAgent = "weather-assistant",
            GenerationId = "gen-weather-123",
            Metadata = ImmutableDictionary
                .Create<string, object>()
                .Add("model", "claude-3-sonnet")
                .Add("temperature", 0.7),
        };

        var toolResultMessage = new ToolsCallResultMessage
        {
            ToolCallResults = [weatherResult],
            Role = Role.User,
            Metadata = ImmutableDictionary
                .Create<string, object>()
                .Add("execution_time", 1250)
                .Add("tool_version", "1.2.0"),
        };

        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage, "weather-agent");

        // Act - Transform using the core transformer
        var transformed = ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);

        // Assert - Verify the transformation
        _ = Assert.IsType<TextMessage>(transformed);

        var textMessage = (TextMessage)transformed;

        // Check basic properties are preserved
        Assert.Equal(Role.Assistant, textMessage.Role);
        Assert.Equal("weather-agent", textMessage.FromAgent);
        Assert.Equal("gen-weather-123", textMessage.GenerationId);

        // Check metadata is preserved (from the aggregate)
        Assert.NotNull(textMessage.Metadata);
        Assert.Equal("claude-3-sonnet", textMessage.Metadata["model"]);
        Assert.Equal(1250, textMessage.Metadata["execution_time"]);

        // Check XML format structure
        var content = textMessage.Text;
        Assert.Contains("<tool_call name=\"GetWeather\">", content);
        Assert.Contains("<tool_response name=\"GetWeather\">", content);
        Assert.Contains("</tool_call>", content);
        Assert.Contains("</tool_response>", content);

        // Check that JSON is pretty-printed
        Assert.Contains("\"location\": \"San Francisco, CA\"", content);
        Assert.Contains("\"unit\": \"celsius\"", content);
        Assert.Contains("\"include_forecast\": true", content);

        // Check response formatting
        Assert.Contains("\"temperature\": 22", content);
        Assert.Contains("\"condition\": \"partly_cloudy\"", content);
        Assert.Contains("\"forecast\": [", content);
    }

    [Fact]
    public void FullPipeline_MultipleToolCalls_WithSeparators()
    {
        // Arrange - Create a scenario with multiple tool calls
        var searchCall = new ToolCall { FunctionName = "SearchDatabase", FunctionArgs = "{\"query\":\"customers in California\",\"limit\":5}" };
        var analysisCall = new ToolCall
        {
            FunctionName = "AnalyzeResults",
            FunctionArgs = "{\"data_source\":\"customer_search\",\"metrics\":[\"count\",\"revenue\"]}"
        };

        var searchResult = new ToolCallResult(
            "search-1",
            "[{\"id\":1,\"name\":\"John Doe\",\"city\":\"San Francisco\",\"revenue\":50000},{\"id\":2,\"name\":\"Jane Smith\",\"city\":\"Los Angeles\",\"revenue\":75000}]"
        );
        var analysisResult = new ToolCallResult(
            "analysis-1",
            "Total customers: 2, Total revenue: $125,000, Average revenue: $62,500"
        );

        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls = ImmutableList.Create(searchCall, analysisCall),
            Role = Role.Assistant,
            GenerationId = "gen-multi-456",
        };

        var toolResultMessage = new ToolsCallResultMessage
        {
            ToolCallResults = ImmutableList.Create(searchResult, analysisResult),
        };

        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage, "data-agent");

        // Act
        var transformed = ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);

        // Assert
        var content = transformed.Text;

        // Check both tool calls are present
        Assert.Contains("<tool_call name=\"SearchDatabase\">", content);
        Assert.Contains("<tool_call name=\"AnalyzeResults\">", content);
        Assert.Contains("<tool_response name=\"SearchDatabase\">", content);
        Assert.Contains("<tool_response name=\"AnalyzeResults\">", content);

        // Check separator between tool calls
        Assert.Contains("---", content);

        // Ensure separator appears between the tool calls (not before first or after last)
        var firstToolCallIndex = content.IndexOf("<tool_call name=\"SearchDatabase\">");
        var separatorIndex = content.IndexOf("---");
        var secondToolCallIndex = content.IndexOf("<tool_call name=\"AnalyzeResults\">");

        Assert.True(firstToolCallIndex < separatorIndex);
        Assert.True(separatorIndex < secondToolCallIndex);
    }

    [Fact]
    public void FullPipeline_MessageSequenceCombination_IntegratesNaturally()
    {
        // Arrange - Create a realistic conversation flow
        var prefixMessage = new TextMessage
        {
            Text =
                "I'll help you analyze your customer data. Let me start by searching for customers in specific regions.",
            Role = Role.Assistant,
            FromAgent = "data-assistant",
            GenerationId = "gen-conv-001",
        };

        var toolCall = new ToolCall
        {
            FunctionName = "GetCustomersByRegion",
            FunctionArgs = "{\"region\":\"West Coast\",\"status\":\"active\",\"sort_by\":\"revenue\"}"
        };
        var toolResult = new ToolCallResult(
            "customer-search-1",
            "{\"count\":42,\"total_revenue\":2500000,\"top_customers\":[{\"name\":\"TechCorp\",\"revenue\":500000},{\"name\":\"StartupXYZ\",\"revenue\":350000}]}"
        );

        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall] };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        var suffixMessage = new TextMessage
        {
            Text =
                "Excellent! Your West Coast region is performing very well with 42 active customers generating $2.5M in total revenue. TechCorp and StartupXYZ are your top performers.",
            Role = Role.Assistant,
            FromAgent = "data-assistant",
            GenerationId = "gen-conv-002",
        };

        var messageSequence = new IMessage[] { prefixMessage, aggregateMessage, suffixMessage };

        // Act
        var combined = ToolsCallAggregateTransformer.CombineMessageSequence(messageSequence);

        // Assert
        _ = Assert.IsType<TextMessage>(combined);
        Assert.Equal(Role.Assistant, combined.Role);
        Assert.Equal("data-assistant", combined.FromAgent);

        var content = combined.Text;

        // Check all parts are included
        Assert.Contains("I'll help you analyze your customer data", content);
        Assert.Contains("<tool_call name=\"GetCustomersByRegion\">", content);
        Assert.Contains("<tool_response name=\"GetCustomersByRegion\">", content);
        Assert.Contains("Excellent! Your West Coast region is performing", content);

        // Check that the content flows properly (basic spacing check)
        Assert.True(content.IndexOf("data.") < content.IndexOf("<tool_call")); // Text before tool call
        Assert.True(content.IndexOf("</tool_response>") < content.IndexOf("Excellent!")); // Tool response before final text

        // Check JSON formatting in tool call
        Assert.Contains("\"region\": \"West Coast\"", content);
        Assert.Contains("\"status\": \"active\"", content);

        // Check JSON formatting in response
        Assert.Contains("\"count\": 42", content);
        Assert.Contains("\"total_revenue\": 2500000", content);
    }

    [Fact]
    public void ExtensionMethods_Integration_WorksSeamlessly()
    {
        // Arrange - Test the extension methods in a realistic scenario
        var toolCall = new ToolCall
        {
            FunctionName = "ProcessPayment",
            FunctionArgs = "{\"amount\":99.99,\"currency\":\"USD\",\"payment_method\":\"credit_card\"}"
        };
        var toolResult = new ToolCallResult(
            "payment-123",
            "{\"status\":\"success\",\"transaction_id\":\"txn_abc123\",\"confirmation_code\":\"CONF789\"}"
        );

        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall] };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage, "payment-processor");

        var conversationMessages = new IMessage[]
        {
            new TextMessage { Text = "Processing your payment now...", Role = Role.Assistant },
            aggregateMessage,
            new TextMessage
            {
                Text = "Payment completed successfully! Your confirmation code is CONF789.",
                Role = Role.Assistant,
            },
        };

        // Act - Test the extension methods

        // 1. Test single message transformation
        var transformedSingle = aggregateMessage.ToNaturalToolUse();
        _ = Assert.IsType<TextMessage>(transformedSingle);
        var transformedText = (TextMessage)transformedSingle;
        Assert.Contains("<tool_call name=\"ProcessPayment\">", transformedText.Text);

        // 2. Test transformability check
        Assert.True(aggregateMessage.IsTransformableToolCall());
        Assert.False(conversationMessages[0].IsTransformableToolCall()); // Regular text message

        // 3. Test collection transformability
        Assert.True(conversationMessages.ContainsTransformableToolCalls());

        var textOnlyMessages = new IMessage[]
        {
            new TextMessage { Text = "Hello", Role = Role.User },
            new TextMessage { Text = "Hi there", Role = Role.Assistant },
        };
        Assert.False(textOnlyMessages.ContainsTransformableToolCalls());

        // 4. Test collection transformation
        var transformedCollection = conversationMessages.ToNaturalToolUse().ToList();
        Assert.Equal(3, transformedCollection.Count);
        Assert.Same(conversationMessages[0], transformedCollection[0]); // First unchanged
        _ = Assert.IsType<TextMessage>(transformedCollection[1]); // Middle transformed
        Assert.Same(conversationMessages[2], transformedCollection[2]); // Last unchanged

        // 5. Test message sequence combination
        var combined = conversationMessages.CombineAsNaturalToolUse();
        Assert.Contains("Processing your payment now...", combined.Text);
        Assert.Contains("<tool_call name=\"ProcessPayment\">", combined.Text);
        Assert.Contains("Payment completed successfully!", combined.Text);
    }

    [Fact]
    public void ErrorHandling_Integration_GracefulFallback()
    {
        // Arrange - Test error scenarios
        var regularMessage = new TextMessage { Text = "This is just text", Role = Role.User };

        // Create aggregate with potentially problematic data
        var problematicToolCall = new ToolCall { FunctionName = null, FunctionArgs = "invalid json {" };
        var problematicToolResult = new ToolCallResult(null, "simple text result");

        var toolCallMessage = new ToolsCallMessage { ToolCalls = [problematicToolCall] };
        var toolResultMessage = new ToolsCallResultMessage
        {
            ToolCallResults = [problematicToolResult],
        };
        var problematicAggregate = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        // Act & Assert - Test graceful handling

        // 1. Regular message should pass through unchanged
        var regularResult = regularMessage.ToNaturalToolUse();
        Assert.Same(regularMessage, regularResult);

        // 2. Problematic aggregate should still transform (gracefully handles null function name)
        var problematicTransformed = problematicAggregate.ToNaturalToolUse();
        _ = Assert.IsType<TextMessage>(problematicTransformed);
        var problematicTextMessage = (TextMessage)problematicTransformed;
        Assert.Contains("UnknownFunction", problematicTextMessage.Text); // Graceful fallback for null name
        Assert.Contains("invalid json {", problematicTextMessage.Text); // Invalid JSON preserved as-is

        // 3. Mixed collection should handle errors gracefully
        var mixedMessages = new IMessage[] { regularMessage, problematicAggregate };
        var mixedResult = mixedMessages.ToNaturalToolUse().ToList();
        Assert.Equal(2, mixedResult.Count);
        Assert.Same(regularMessage, mixedResult[0]); // First unchanged
        _ = Assert.IsType<TextMessage>(mixedResult[1]); // Second transformed despite issues

        // 4. Combination should work even with problematic messages
        var combinedResult = mixedMessages.CombineAsNaturalToolUse();
        Assert.Contains("This is just text", combinedResult.Text);
        Assert.Contains("UnknownFunction", combinedResult.Text);
    }

    [Fact]
    public void FullPipeline_RealWorldScenario_CompleteWorkflow()
    {
        // Arrange - Simulate a complete AI assistant interaction
        var userMessage = new TextMessage
        {
            Text = "Can you help me find the total sales for Q1 and create a summary report?",
            Role = Role.User,
        };

        var assistantReply = new TextMessage
        {
            Text =
                "I'll help you get the Q1 sales data and create a summary report. Let me start by retrieving the sales data.",
            Role = Role.Assistant,
            FromAgent = "sales-assistant",
        };

        // First tool call - get sales data
        var salesCall = new ToolCall
        {
            FunctionName = "GetSalesData",
            FunctionArgs = "{\"period\":\"Q1 2024\",\"include_breakdown\":true,\"currency\":\"USD\"}"
        };
        var salesResult = new ToolCallResult(
            "sales-001",
            "{\"total_sales\":1750000,\"breakdown\":{\"January\":580000,\"February\":620000,\"March\":550000},\"transactions\":156,\"average_order\":11218}"
        );

        var salesCallMessage = new ToolsCallMessage { ToolCalls = [salesCall] };
        var salesResultMessage = new ToolsCallResultMessage { ToolCallResults = [salesResult] };
        var salesAggregate = new ToolsCallAggregateMessage(salesCallMessage, salesResultMessage);

        var analysisMessage = new TextMessage
        {
            Text = "Great! Now let me analyze this data and create a summary report for you.",
            Role = Role.Assistant,
            FromAgent = "sales-assistant",
        };

        // Second tool call - create report
        var reportCall = new ToolCall
        {
            FunctionName = "CreateSummaryReport",
            FunctionArgs = "{\"data_source\":\"Q1_sales\",\"format\":\"executive_summary\",\"include_charts\":false}"
        };
        var reportResult = new ToolCallResult(
            "report-001",
            "Q1 2024 Sales Summary:\\n\\nTotal Revenue: $1,750,000\\nTransactions: 156\\nAverage Order Value: $11,218\\n\\nMonthly Breakdown:\\n- January: $580,000 (33.1%)\\n- February: $620,000 (35.4%)\\n- March: $550,000 (31.4%)\\n\\nKey Insights:\\n- February was the strongest month\\n- Consistent performance across the quarter\\n- High average order value indicates quality customer base"
        );

        var reportCallMessage = new ToolsCallMessage { ToolCalls = [reportCall] };
        var reportResultMessage = new ToolsCallResultMessage { ToolCallResults = [reportResult] };
        var reportAggregate = new ToolsCallAggregateMessage(reportCallMessage, reportResultMessage);

        var finalMessage = new TextMessage
        {
            Text =
                "Perfect! I've compiled your Q1 sales summary. The quarter shows strong performance with $1.75M in total revenue and consistent monthly results.",
            Role = Role.Assistant,
            FromAgent = "sales-assistant",
        };

        var fullConversation = new IMessage[]
        {
            userMessage,
            assistantReply,
            salesAggregate,
            analysisMessage,
            reportAggregate,
            finalMessage,
        };

        // Act - Transform the entire conversation
        var transformedConversation = fullConversation.ToNaturalToolUse().ToList();
        var combinedResponse = fullConversation.Skip(1).CombineAsNaturalToolUse(); // Skip user message

        // Assert - Verify the transformation maintains conversation flow
        Assert.Equal(6, transformedConversation.Count);

        // First message (user) unchanged
        Assert.Same(userMessage, transformedConversation[0]);

        // Second message (assistant) unchanged
        Assert.Same(assistantReply, transformedConversation[1]);

        // Third message (sales aggregate) transformed
        _ = Assert.IsType<TextMessage>(transformedConversation[2]);
        var salesTransformed = (TextMessage)transformedConversation[2];
        Assert.Contains("<tool_call name=\"GetSalesData\">", salesTransformed.Text);
        Assert.Contains("\"period\": \"Q1 2024\"", salesTransformed.Text);
        Assert.Contains("\"total_sales\": 1750000", salesTransformed.Text);

        // Fifth message (report aggregate) transformed
        _ = Assert.IsType<TextMessage>(transformedConversation[4]);
        var reportTransformed = (TextMessage)transformedConversation[4];
        Assert.Contains("<tool_call name=\"CreateSummaryReport\">", reportTransformed.Text);
        Assert.Contains("Q1 2024 Sales Summary:", reportTransformed.Text);

        // Verify proper content flow and structure
        Assert.Contains("I'll help you get the Q1 sales data", combinedResponse.Text);
        Assert.Contains("<tool_call name=\"GetSalesData\">", combinedResponse.Text);
        Assert.Contains("<tool_response name=\"GetSalesData\">", combinedResponse.Text);
        Assert.Contains("Great! Now let me analyze", combinedResponse.Text);
        Assert.Contains("<tool_call name=\"CreateSummaryReport\">", combinedResponse.Text);
        Assert.Contains("Perfect! I've compiled your Q1 sales summary", combinedResponse.Text);

        // Verify content ordering
        var helpIndex = combinedResponse.Text.IndexOf("I'll help you get");
        var firstToolCallIndex = combinedResponse.Text.IndexOf("<tool_call name=\"GetSalesData\">");
        var greatIndex = combinedResponse.Text.IndexOf("Great! Now let me");
        var secondToolCallIndex = combinedResponse.Text.IndexOf("<tool_call name=\"CreateSummaryReport\">");
        var perfectIndex = combinedResponse.Text.IndexOf("Perfect! I've compiled");

        Assert.True(helpIndex < firstToolCallIndex);
        Assert.True(firstToolCallIndex < greatIndex);
        Assert.True(greatIndex < secondToolCallIndex);
        Assert.True(secondToolCallIndex < perfectIndex);
    }
}
