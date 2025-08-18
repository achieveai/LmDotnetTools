# Function Provider Patterns Analysis

## IFunctionProvider Interface
- **ProviderName**: String identifier for debugging and conflict resolution
- **Priority**: Integer for ordering providers (lower numbers = higher priority)
- **GetFunctions()**: Returns collection of FunctionDescriptor objects

## Key Implementation Patterns

### 1. TypeFunctionProvider (Attribute-Based)
- Uses `[Function("function-name", "description")]` attributes
- Uses `[Description("description")]` attributes for methods without Function attribute
- Supports both static methods (AddFunctionsFromType) and instance methods (AddFunctionsFromObject)
- Automatically converts PascalCase method names to kebab-case for LLM exposure
- Handles parameter mapping via reflection and JSON deserialization

### 2. Custom Function Provider (Manual Registration)
- Direct creation of FunctionContract and handler delegate
- Full control over function name, description, parameters
- Example from ExamplePythonMCPClient shows manual JSON parsing and delegation

## Function Registration Process
1. Create FunctionContract with name, description, parameters
2. Create handler delegate: `Func<string, Task<string>>`
3. Wrap in FunctionDescriptor with contract, handler, and provider name
4. Return from GetFunctions()

## Parameter Handling
- Parameters defined as FunctionParameterContract
- JSON schema objects for type definitions
- Handler receives JSON string, deserializes to extract parameters
- Return string result (can be JSON for complex responses)

## Integration with Middleware
- FunctionRegistry collects providers
- BuildMiddleware() creates FunctionCallMiddleware
- Functions exposed to LLM with kebab-case names
- Automatic JSON parameter mapping and response handling
