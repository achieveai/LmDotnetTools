# User Feedback and Learnings - TodoManager

## Requirements Gathered ✅ COMPLETE

### Storage
- **In-memory only** - No persistence required across runs
- Tasks will be lost when the application restarts

### Core Functions (from initial request)
1. `add-task`: Adds new task with default "not started" status. Supports parent-child relationships (2 levels only)
2. `update-task`: Updates task status to "not started", "in progress", "completed", or "removed"
3. `add-task-notes`: Adds/updates notes for existing tasks
4. `list-tasks`: Lists all current tasks with status and notes
5. `GetMarkdown()`: Creates markdown representation of all tasks

### Function Naming
- Exposed to FunctionCallMiddleware: kebab-case (add-task, update-task, etc.)
- C# methods: PascalCase (AddTask, UpdateTask, etc.)

### Return Format
- **Plain text in markdown format** for all function responses
- Human-readable and easily displayable for LLM integration

### Task IDs
- **Auto-incrementing integers** (1, 2, 3, 4...)
- Simple and matches the provided example format

### Task Structure (from example)
- Two-level hierarchy: Main tasks and subtasks
- Status indicators: [-] (in progress), [x] (completed), [ ] (not started), [?] (removed)
- Notes are numbered and associated with tasks
- Markdown format shows hierarchy with indentation
