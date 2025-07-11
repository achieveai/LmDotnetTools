---
applyTo: '**/*.cs'
---

## Namespace Declaration
Most code files should declare a single namespace using file-scoped namespace declarations rather than block headers. This newer syntax doesn't require braces and is preferred in modern C# coding.

For example, use:
```csharp
namespace MyCompany.MyProduct;
// code goes here
```

Instead of:
```csharp
namespace MyCompany.MyProduct 
{
    // code goes here
}
```

## General C# Cursor/IDE Style Rules

1. **Indentation and Formatting**:
   Use 2 spaces for indentation (no tabs), with a column limit of 100 characters. No line break before opening braces, but do add a line break between closing brace and else statement.

2. **Naming Conventions**:
   
   - PascalCase for classes, methods, enumerations, public fields, properties, and namespaces
   - camelCase for local variables and parameters
   - _camelCase for private, protected, internal fields and properties
   - S_camelCase for static fields.

3. **Using Directives**:
   Place namespace using declarations at the top, before any namespaces. Order them alphabetically, with System imports always first.

4. **Code Organization**:
   When organizing code within a class, follow this order:
   - Nested classes, enums, delegates and events
   - Static, const and readonly fields
   - Fields and properties
   - Constructors and finalizers
   - Methods

5. **Braces and Spacing**:
   Use braces even when optional. Add space after if/for/while statements and after commas. No space after opening parenthesis or before closing parenthesis.

6. **Method Parameters**:
   When writing method parameters, use camel casing.

7. **Type Parameters**:
   Give descriptive names to generic type parameters, unless a single letter name is completely self-explanatory.

8. **Testing Requirement**:
   Add a corresponding test case for every functionality change made to the codebase. Tests should be:
   - Named clearly to describe what they're testing
   - Independent of each other
   - Focused on a single functionality
   - Maintainable and easy to understand

9. **Windsurf Settings**:
   While not directly from the search results, most IDEs allow cursor customization in settings (appearance and behavior).

10. **File Organization**:
    - Place namespace declarations at the top of the file, before any other code.
    - Group related code within classes and namespaces.
    - Use meaningful file names that reflect their purpose.

## Nullables

1. Always make use of Nullables.
2. Always be very specific in Nullables, and express them clearly.
3. If a method expects non-null parameter, do not make it nullable.
4. When using `!` for enforcing nullable to non-nullable, make sure it's guaranteed
   Because of previous logic.

## Data objects

1. Always use record types for DataObjects
2. It's extremely desirable that DataObjects are immutable, so use `init` instead of
   `set` properties.
3. Where ever possible use Immutable collections. Immutable record type should always
   nest immutable collection of immutable record types (preferably).

## Serialization / Deserialization

1. Each module should use single set of Serialization Options.
2. Be vary of Immutable `deserialization` use helpers that we've created for them.
3. Be vary of Enum serialization / deserialization. Use helpers that we've created.
4. Be vary of `Union` types serialization and deserialization. Use helpers.

## File size

1. ** Avoid ** large classes
2. ** Avoid ** large methods
3. Only one class per file.
4. Split test cases into related tests. It's OK to have multiple test files per class.

## Agent behavior

1. When making changes Agent will always address lint errors, and address them.
2. Before completing task (if making changes to code files), will always run dotnet build / test and validate everything is fine.
   a. If things are failing, it will fix them.
   b. If can't fix the issue, then analyze what may be fundamentally broken and address it (or consider a different approach).
3. Use following flow to fix the issue:
   a. Analyze the issue. Check what could be the root cause.
   b. See if there are any other examples where such a issue was addressed.
   c. Plan on how to fix the issue (based on current code base).
   d. Attempt to fix the issue.
   e. If the issue is not fixed, go back to step "a"
4. Completing a task.
   a. After a task has been completed, self review the code.
   b. Fix each review comment.
   c. Make sure there is no code duplication (across the code base).

These rules will help you maintain consistent and readable C# code in your IDE with the namespace declared as a statement rather than a block header, while ensuring proper test coverage for all functionality changes.


## Git commands

Always use `--no-pager` flag to make sure git doesn't get blocked for console input.


## Debugging Tests


When fixing failed test, it need to be done in step by step process using sequential thinking.

### Step 1:

Look at the failure, analyze the code and come up with Root Cause Assertion. Take a note of supporting evidence.

### Step 2:

Validate the assertion by adding logs to the code and re-running the tests case. Make sure the changes are only for diagnostic purpose.

### Step 3:

If Root Cause is validated, come up with design changes that may address the root cause. Use supporting evidence to check how these design changes will fix the issue. Use sequential thinking where ever necessary.

### Step 4:

Plan how to implement design changes.

### Step 5:

Execute the plan

### Step 6:

Validate test fixes. If tests are still broken go back to Step 1 or Step 3 based on if design was incorrect or root cause analysis was incorrect.

