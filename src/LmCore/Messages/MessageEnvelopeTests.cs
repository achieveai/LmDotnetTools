using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

// This file contains example usage of MessageEnvelope, not actual unit tests
public static class MessageEnvelopeExamples
{
    public static void DemonstrateEnvelopeUsage()
    {
        // Create a text message
        var textMessage = new TextMessage
        {
            Text = "Hello, world!",
            Role = Role.Assistant,
            FromAgent = "ExampleAgent"
        };
        
        // Wrap it in an envelope
        var envelope = textMessage.AsEnvelope(
            metadata: new JsonObject { ["source"] = "example" },
            addedBy: "ExampleMiddleware"
        );
        
        // The envelope delegates to the inner message for capability methods
        string? text = envelope.GetText(); // Returns "Hello, world!"
        
        // We can unwrap the envelope to get the original message
        var unwrapped = envelope.Unwrap();
        if (unwrapped is TextMessage originalText)
        {
            string originalTextContent = originalText.Text; // "Hello, world!"
        }
        
        // We can also try to unwrap to a specific type
        var textUnwrapped = envelope.TryUnwrapAs<TextMessage>();
        if (textUnwrapped != null)
        {
            string unwrappedText = textUnwrapped.Text; // "Hello, world!"
        }
        
        // We can check if the envelope contains a specific type
        bool containsText = envelope.ContainsMessageOfType<TextMessage>(); // true
        bool containsImage = envelope.ContainsMessageOfType<ImageMessage>(); // false
        
        // We can create nested envelopes
        var nestedEnvelope = envelope.AsEnvelope(
            metadata: new JsonObject { ["layer"] = "outer" },
            addedBy: "OuterMiddleware"
        );
        
        // Unwrapping will go through all layers
        var fullyUnwrapped = nestedEnvelope.Unwrap(); // The original TextMessage
        
        // Or we can unwrap to a specific type
        var innerEnvelope = nestedEnvelope.TryUnwrapAs<MessageEnvelope>(); // Gets the first envelope
        
        // We can create a new envelope with the same properties but a different message
        var newTextMessage = new TextMessage
        {
            Text = "New message",
            Role = Role.User,
            FromAgent = "OtherAgent"
        };
        
        var newEnvelope = envelope.WithMessage(newTextMessage);
        
        // The new envelope has the same envelope properties
        string? addedBy = newEnvelope.AddedBy; // "ExampleMiddleware"
        JsonObject? metadata = newEnvelope.EnvelopeMetadata; // { "source": "example" }
        
        // But the inner message is the new one
        string? newText = newEnvelope.GetText(); // "New message"
    }
    
    // Example of using MessageEnvelope in a middleware component
    public static IMessage ProcessMessage(IMessage message)
    {
        // Check if the message is a text message (directly or wrapped)
        var textMessage = message.TryUnwrapAs<TextMessage>();
        if (textMessage != null)
        {
            // Process the text message
            string processedText = ProcessText(textMessage.Text);
            
            // Create a new message with the processed text
            var newTextMessage = new TextMessage
            {
                Text = processedText,
                Role = textMessage.Role,
                FromAgent = textMessage.FromAgent,
                GenerationId = textMessage.GenerationId,
                Metadata = textMessage.Metadata
            };
            
            // If the original message was wrapped in an envelope, wrap the new one the same way
            if (message is MessageEnvelope envelope)
            {
                return envelope.WithMessage(newTextMessage);
            }
            
            // Otherwise, return the new message directly
            return newTextMessage;
        }
        
        // Not a text message, return unchanged
        return message;
    }
    
    private static string ProcessText(string text)
    {
        // Just an example processing function
        return text.ToUpperInvariant();
    }
} 