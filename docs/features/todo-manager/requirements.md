# Feature Specification: Todo Manager

## High-Level Overview

The Todo Manager is an in-memory task management system that provides LLM-accessible functions for creating, managing, and displaying hierarchical todo lists. It supports two-level task hierarchies (main tasks and subtasks), status tracking, note-taking, and markdown output formatting.

## High Level Requirements

- **In-memory task storage** - No persistence across application restarts
- **Function call integration** - Expose functions via FunctionCallMiddleware with kebab-case names
- **Two-level hierarchy** - Support main tasks with optional subtasks
- **Status management** - Track task progress through defined status states
- **Note management** - Add and update notes for any task
- **Markdown output** - Generate human-readable task lists in markdown format
- **Auto-incrementing IDs** - Simple integer-based task identification

## Existing Solutions

### Current Codebase Patterns
- **TypeFunctionProvider**: Attribute-based function registration using `[Function()]` and `[Description()]`
- **Custom Function Providers**: Manual function contract creation with full control over parameters
- **FunctionCallMiddleware**: Integration layer that handles JSON parameter parsing and response formatting

### External Research
- Common todo management patterns use hierarchical task structures
- Status enums for lifecycle management (NotStarted, InProgress, Completed, Removed)
- ID-based task operations for efficient lookups
- Recursive operations for handling parent-child relationships

## Current Implementation

This is a new feature. No existing implementation exists in the codebase.

## Detailed Requirements

### Requirement 1: Task Model and Storage
- **User Story**: As a user, I need a way to represent tasks with hierarchical relationships and metadata so that I can organize my work effectively.

#### Acceptance Criteria:
1. **Task Structure**: WHEN creating the task model THEN it SHALL include properties for ID (int), Title (string), Status (enum), Notes (List<string>), and SubTasks (List<Task>)
2. **Status Enum**: WHEN defining task status THEN it SHALL support "NotStarted", "InProgress", "Completed", and "Removed" states
3. **Hierarchy Limits**: WHEN adding subtasks THEN the system SHALL enforce a maximum depth of 2 levels (main task + subtask only)
4. **In-Memory Storage**: WHEN storing tasks THEN they SHALL be kept in memory only with no persistence

### Requirement 2: Add Task Function
- **User Story**: As a user, I need to add new tasks and subtasks so that I can build my todo list.

#### Acceptance Criteria:
1. **Function Exposure**: WHEN registering functions THEN "add-task" SHALL be available to LLM with parameters for title and optional parent_id
2. **Main Task Creation**: WHEN calling add-task without parent_id THEN it SHALL create a new main task with auto-incremented ID and "NotStarted" status
3. **Subtask Creation**: WHEN calling add-task with valid parent_id THEN it SHALL create a subtask under the specified parent
4. **Error Handling**: WHEN calling add-task with invalid parent_id THEN it SHALL return an error message in markdown format
5. **Response Format**: WHEN task is successfully created THEN it SHALL return confirmation message with task ID in markdown format

### Requirement 3: Update Task Status Function
- **User Story**: As a user, I need to update task status so that I can track progress on my work.

#### Acceptance Criteria:
1. **Function Exposure**: WHEN registering functions THEN "update-task" SHALL be available with parameters for task_id and status
2. **Status Validation**: WHEN updating status THEN it SHALL only accept "not started", "in progress", "completed", or "removed"
3. **Task Lookup**: WHEN updating task status THEN it SHALL find tasks by ID across all hierarchy levels
4. **Error Handling**: WHEN task_id is invalid THEN it SHALL return error message in markdown format
5. **Response Format**: WHEN status is successfully updated THEN it SHALL return confirmation message in markdown format

### Requirement 4: Add Task Notes Function
- **User Story**: As a user, I need to add notes to tasks so that I can capture additional context and details.

#### Acceptance Criteria:
1. **Function Exposure**: WHEN registering functions THEN "add-task-notes" SHALL be available with parameters for task_id and note
2. **Note Appending**: WHEN adding notes THEN it SHALL append to the existing notes list for the task
3. **Task Lookup**: WHEN adding notes THEN it SHALL find tasks by ID across all hierarchy levels
4. **Error Handling**: WHEN task_id is invalid THEN it SHALL return error message in markdown format
5. **Response Format**: WHEN note is successfully added THEN it SHALL return confirmation message in markdown format

### Requirement 5: List Tasks Function
- **User Story**: As a user, I need to view all my tasks so that I can see what work needs to be done.

#### Acceptance Criteria:
1. **Function Exposure**: WHEN registering functions THEN "list-tasks" SHALL be available with no parameters
2. **Hierarchical Display**: WHEN listing tasks THEN it SHALL show main tasks with indented subtasks
3. **Status Indicators**: WHEN displaying tasks THEN it SHALL use [ ] for not started, [-] for in progress, [x] for completed
4. **Notes Display**: WHEN tasks have notes THEN it SHALL display them indented under the task with numbering
5. **Markdown Format**: WHEN returning task list THEN it SHALL use markdown formatting matching the provided example

### Requirement 6: Markdown Generation Method
- **User Story**: As a developer, I need a method to generate markdown representation so that I can get formatted output programmatically.

#### Acceptance Criteria:
1. **Method Availability**: WHEN implementing TodoManager THEN it SHALL provide a GetMarkdown() method
2. **Format Consistency**: WHEN generating markdown THEN it SHALL match the same format as list-tasks function
3. **Complete Output**: WHEN calling GetMarkdown THEN it SHALL include all tasks, subtasks, and notes
4. **Header Inclusion**: WHEN generating markdown THEN it SHALL include "# TODO" header

### Requirement 7: Function Provider Integration
- **User Story**: As a developer, I need the TodoManager to integrate with the function call system so that LLMs can use it.

#### Acceptance Criteria:
1. **Provider Implementation**: WHEN creating TodoManager THEN it SHALL implement IFunctionProvider interface
2. **Function Registration**: WHEN getting functions THEN it SHALL return FunctionDescriptor objects for all four operations
3. **Parameter Mapping**: WHEN functions are called THEN it SHALL properly deserialize JSON parameters
4. **Error Handling**: WHEN operations fail THEN it SHALL return descriptive error messages instead of throwing exceptions
5. **Provider Priority**: WHEN registering provider THEN it SHALL use appropriate priority for conflict resolution
