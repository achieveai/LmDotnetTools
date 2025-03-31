# Prompts Management.

Prompts are managed in YAML file format.

## Prompt Name and Versioning
The key of the YAML dictionary is the prompt name. This key then contains nested 
dictionary with version number as key.

Example:
```yaml
MyPrompt:
  v1.1: Some prompt text.
  v1.2: Some updated prompt text.
```

In above case, the prompt name is 'MyPrompt' and the prompt version is '1.1' and '1.2'.

## Prompt Types
There are two types of prompts supported:

1. Simple Prompts: A single string containing the prompt text with template variables.
2. Prompt Chains: A sequence of messages with roles (system, user, assistant) and content.

### Simple Prompt Example
```yaml
SimplePrompt:
  v1.0: "Hello {{name}}, welcome to {{company}}."
```

### Prompt Chain Example
```yaml
ChainPrompt:
  v1.0:
    - system: "You are a helpful assistant for {{company}}."
    - user: "What can you tell me about {{topic}}?"
    - assistant: "Here's what I know about {{topic}}..."
```

## Template Variables
The prompt text supports Scriban templating syntax. Template variables are enclosed in 
double curly braces and have the following format:
```
{{variable_name}}
```

Scriban templating also supports advanced features like loops and conditionals:
```yaml
LoopPrompt:
  v1.0: |
    Items:
    {{ for item in items }}
    - {{item}}
    {{ end }}
```

# The Prompt Reader

The Prompt Reader reads YAML files and manages prompts with versioning. It supports both 
simple prompts and prompt chains.

## Constructing the Prompt Reader

The Prompt Reader has 3 constructors:
1. Takes a file path and reads the YAML file
2. Takes a stream and reads the YAML file from the stream
3. Takes an Assembly name and Resource name to read the YAML file from embedded resources

## Getting Prompts

The prompt reader provides the following API:

```csharp
public interface IPromptReader
{
    Prompt GetPrompt(string promptName, string version = "latest");
    PromptChain GetPromptChain(string promptName, string version = "latest");
}

public record Prompt(string Name, string Version, string Value)
{
    public virtual string PromptText(Dictionary<string, object>? variables = null);
}

public record PromptChain(string Name, string Version, List<Message> Messages) 
    : Prompt(Name, Version, string.Empty)
{
    public List<Message> PromptMessages(Dictionary<string, object>? variables = null);
}

public struct Message
{
    public string Role { get; }
    public string Content { get; }
}
```

## Version Management

The Prompt Reader automatically adds a "latest" version to each prompt, which points to 
the highest version number available. When requesting a prompt without specifying a 
version, the "latest" version is returned.

## Template Processing

Templates are processed using the Scriban templating engine, which provides:
- Variable substitution
- Loops and conditionals
- Advanced text manipulation features
- Safe template execution

When applying variables to prompts:
1. For simple prompts, variables are applied to the prompt text
2. For prompt chains, variables are applied to each message's content
3. If no variables are provided, the template text is returned as-is

## Message Roles

In prompt chains, message roles are restricted to:
- system
- user
- assistant

Any other role will result in an exception.
