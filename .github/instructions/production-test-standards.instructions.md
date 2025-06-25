---
applyTo: '**/*'
---

# Cursor Rules for Production and Test Code

## Introduction
This document contains rules and guidelines for Cursor AI to follow when assisting with code in this project. These rules ensure that generated code meets quality standards for both production and test code.

## Project Structure Rules
---
Description: Core Project Guidelines
Globs: *
---

## Production Code Standards

### Code Duplication
- Never duplicate code across the codebase
- Extract repeated logic into reusable functions, methods, or classes
- Identify and refactor any duplicated code patterns
- Use inheritance, composition, or utilities for shared functionality
- Before implementing something new, check if similar functionality already exists

### Code Organization
- Follow a consistent and clean code structure
- Group related functionality into well-named classes or modules
- Use appropriate design patterns for the problem at hand
- Separate concerns: UI logic, business logic, data access, etc.
- Keep files focused on a single responsibility
- Use meaningful directory structure that reflects the domain
- Keep modules and functions small and focused

### Logging
- Implement comprehensive and consistent logging
- Add appropriate log levels (DEBUG, INFO, WARNING, ERROR)
- Include contextual information in log messages (request IDs, user IDs)
- Log entry and exit points of critical paths
- Include meaningful error messages with stack traces when exceptions occur
- Log important business events for audit trail
- Don't log sensitive information (passwords, tokens, PII)
- Include timestamps in log formats

### Design Patterns
- Use appropriate design patterns for the problem domain
- Follow SOLID principles (Single Responsibility, Open/Closed, Liskov Substitution, Interface Segregation, Dependency Inversion)
- Implement dependency injection for better testability
- Consider using factory patterns for object creation
- Use builder patterns for complex object construction
- Implement repository patterns for data access
- Consider observer patterns for event-driven architectures
- Apply strategy patterns for varying algorithms

## Test Code Standards

### Testing Production Code
- Ensure every production code unit has corresponding test(s)
- Tests should verify both expected functionality and edge cases
- Use descriptive test names that explain the test's purpose
- Structure tests using Arrange-Act-Assert pattern
- Mock external dependencies appropriately
- Test both happy paths and error paths
- Include integration tests for system components
- Verify that tests are actually testing the intended behavior

### Diagnostic Logging
- Add detailed diagnostic logs in test code for debugging
- Log test setup and teardown processes
- Include expected vs. actual values in test assertions
- Log environment and configuration details that affect tests
- Use DEBUG level for test diagnostic information
- Provide context in logs to help identify test failures
- Format logs to be easily readable in CI/CD pipelines

### Avoiding Code Duplication
- Don't duplicate test setup code across test files
- Use test fixtures, factories, or builders for common test data
- Implement shared setup and teardown methods
- Create helper methods for common assertion patterns
- Avoid copy-pasting test code between similar tests
- Leverage parameterized tests for testing similar scenarios

### Test Utilities
- Move common test code to a dedicated TestUtilities project
- Create reusable test data generators
- Implement common assertion helpers
- Add utilities for setting up test environments
- Create mocks and stubs for commonly used dependencies
- Provide utilities for database setup/teardown
- Implement helpers for authentication/authorization in tests
- Create utilities for network/API simulation