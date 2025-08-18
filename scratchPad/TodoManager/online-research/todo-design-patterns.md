# Todo Management Design Patterns

## Key Design Elements (from web research)

### Task Model Structure
```csharp
public enum TaskStatus
{
    NotStarted,
    InProgress, 
    Completed,
    Removed
}

public class Task
{
    public int Id { get; set; }
    public string Title { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.NotStarted;
    public List<string> Notes { get; set; } = new List<string>();
    public List<Task> SubTasks { get; set; } = new List<Task>();
}
```

### Core Operations Pattern
1. **Add Task**: Auto-increment ID, support parent-child relationships
2. **Update Status**: Find task by ID, update status enum
3. **Add Notes**: Append to notes list
4. **List Tasks**: Return all root tasks with their subtasks
5. **Generate Output**: Create structured representation (markdown/JSON)

### Hierarchy Management
- Two-level max: Main tasks and subtasks
- Parent task contains list of subtasks
- Recursive search through hierarchy for operations
- Flat ID space across all tasks for easy lookup

### Status Mapping for Markdown
- `NotStarted` → `[ ]` (empty checkbox)
- `InProgress` → `[-]` or `[~]` (dash/tilde)
- `Completed` → `[x]` (checked)
- `Removed` → Special handling (could be `[d]` for deleted)

### Notes Structure
- List of strings per task
- Displayed with indentation under task
- Numbered format in markdown output
- Could include timestamps or metadata

## Considerations for LLM Integration
- String-based parameters for function calls
- JSON serialization for complex parameters
- Clear error messages for invalid operations
- Consistent return format (JSON or markdown)
- ID-based operations for task management
