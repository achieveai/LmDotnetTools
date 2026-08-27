using AchieveAi.LmDotnetTools.LmCore.Tools;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Tools;

/// <summary>
///     Covers the acceptance criteria in <c>docs/features/todo-manager/requirements.md</c>.
/// </summary>
public class TodoManagerTests
{
    private static async Task<(string Text, bool IsError)> InvokeAsync(
        TodoManager manager,
        string functionName,
        string argsJson
    )
    {
        var descriptor = manager.GetFunctions().Single(f => f.Contract.Name == functionName);
        var result = await descriptor.Handler(argsJson, new ToolCallContext(), CancellationToken.None);
        var resolved = Assert.IsType<ToolHandlerResult.Resolved>(result);
        return (resolved.Payload.Text, resolved.Payload.IsError);
    }

    // ---- Requirement 7: Function provider integration ----

    [Fact]
    public void GetFunctions_ExposesTheFourKebabCaseOperations()
    {
        var manager = new TodoManager();

        var names = manager.GetFunctions().Select(f => f.Contract.Name).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(["add-task", "add-task-notes", "list-tasks", "update-task"], names);
    }

    [Fact]
    public void GetFunctions_DeclaresParametersMatchingTheSpec()
    {
        var manager = new TodoManager();
        var functions = manager.GetFunctions().ToDictionary(f => f.Contract.Name);

        var addTask = functions["add-task"].Contract.Parameters!.ToList();
        Assert.Equal(["title", "parent_id"], addTask.Select(p => p.Name));
        Assert.True(addTask.Single(p => p.Name == "title").IsRequired);
        Assert.False(addTask.Single(p => p.Name == "parent_id").IsRequired);

        var updateTask = functions["update-task"].Contract.Parameters!.ToList();
        Assert.Equal(["task_id", "status"], updateTask.Select(p => p.Name));
        Assert.True(updateTask.All(p => p.IsRequired));

        var addNotes = functions["add-task-notes"].Contract.Parameters!.ToList();
        Assert.Equal(["task_id", "note"], addNotes.Select(p => p.Name));
        Assert.True(addNotes.All(p => p.IsRequired));

        Assert.Empty(functions["list-tasks"].Contract.Parameters!);
    }

    [Fact]
    public void Provider_ReportsNameAndPriority()
    {
        var manager = new TodoManager();

        Assert.Equal("TodoManager", manager.ProviderName);
        Assert.Equal(100, manager.Priority);
        Assert.All(manager.GetFunctions(), f => Assert.Equal("TodoManager", f.ProviderName));
    }

    [Fact]
    public async Task Handlers_DeserializeJsonParameters()
    {
        var manager = new TodoManager();

        var added = await InvokeAsync(manager, "add-task", """{"title":"Ship the parser"}""");
        Assert.False(added.IsError);

        var sub = await InvokeAsync(manager, "add-task", """{"title":"Write the lexer","parent_id":1}""");
        Assert.False(sub.IsError);

        var updated = await InvokeAsync(manager, "update-task", """{"task_id":2,"status":"completed"}""");
        Assert.False(updated.IsError);

        var noted = await InvokeAsync(manager, "add-task-notes", """{"task_id":2,"note":"Handles unicode"}""");
        Assert.False(noted.IsError);

        var listed = await InvokeAsync(manager, "list-tasks", "{}");
        Assert.Equal(manager.GetMarkdown(), listed.Text);
        Assert.Contains("- [x] 2. Write the lexer", listed.Text, StringComparison.Ordinal);
        Assert.Contains("Handles unicode", listed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handlers_ReturnErrorResultsInsteadOfThrowing()
    {
        var manager = new TodoManager();

        var malformed = await InvokeAsync(manager, "add-task", "not json at all");
        Assert.True(malformed.IsError);

        var missingTitle = await InvokeAsync(manager, "add-task", """{"parent_id":1}""");
        Assert.True(missingTitle.IsError);

        var unknownParent = await InvokeAsync(manager, "add-task", """{"title":"x","parent_id":99}""");
        Assert.True(unknownParent.IsError);

        var unknownTask = await InvokeAsync(manager, "update-task", """{"task_id":99,"status":"completed"}""");
        Assert.True(unknownTask.IsError);
    }

    // ---- Requirement 2: add-task ----

    [Fact]
    public void AddTask_WithoutParent_CreatesMainTaskWithIncrementingIdAndNotStartedStatus()
    {
        var manager = new TodoManager();

        var first = manager.AddTask("Ship the parser", parentId: null);
        var second = manager.AddTask("Ship the printer", parentId: null);

        Assert.Contains("1", first, StringComparison.Ordinal);
        Assert.Contains("2", second, StringComparison.Ordinal);

        var tasks = manager.Tasks;
        Assert.Equal([1, 2], tasks.Select(t => t.Id));
        Assert.All(tasks, t => Assert.Equal(TodoTaskStatus.NotStarted, t.Status));
        Assert.Equal("Ship the parser", tasks[0].Title);
    }

    [Fact]
    public void AddTask_WithValidParentId_NestsTheTaskUnderThatParent()
    {
        var manager = new TodoManager();
        _ = manager.AddTask("Ship the parser", parentId: null);
        _ = manager.AddTask("Unrelated main task", parentId: null);

        _ = manager.AddTask("Write the lexer", parentId: 1);

        var parent = manager.Tasks.Single(t => t.Id == 1);
        var sibling = manager.Tasks.Single(t => t.Id == 2);

        // The subtask must live under its parent, not at the root and not under the sibling.
        Assert.Equal(2, manager.Tasks.Count);
        Assert.Equal([3], parent.SubTasks.Select(t => t.Id));
        Assert.Equal("Write the lexer", parent.SubTasks[0].Title);
        Assert.Empty(sibling.SubTasks);
    }

    [Fact]
    public void AddTask_WithUnknownParentId_IsRejectedAndAddsNothing()
    {
        var manager = new TodoManager();
        _ = manager.AddTask("Ship the parser", parentId: null);

        var result = manager.AddTask("Orphan", parentId: 99);

        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
        Assert.Single(manager.Tasks);
        Assert.Empty(manager.Tasks[0].SubTasks);
    }

    [Fact]
    public void AddTask_UnderASubtask_IsRejectedByTheTwoLevelDepthLimit()
    {
        var manager = new TodoManager();
        _ = manager.AddTask("Ship the parser", parentId: null);
        _ = manager.AddTask("Write the lexer", parentId: 1);

        // Task 2 is itself a subtask, so it may not become a parent.
        var result = manager.AddTask("Handle unicode", parentId: 2);

        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
        var subtask = manager.Tasks[0].SubTasks.Single();
        Assert.Empty(subtask.SubTasks);
    }

    // ---- Requirement 3: update-task ----

    [Theory]
    [InlineData("not started", TodoTaskStatus.NotStarted)]
    [InlineData("in progress", TodoTaskStatus.InProgress)]
    [InlineData("completed", TodoTaskStatus.Completed)]
    [InlineData("removed", TodoTaskStatus.Removed)]
    [InlineData("In Progress", TodoTaskStatus.InProgress)]
    public void UpdateTask_AcceptsTheSpecifiedStatusValues(string status, TodoTaskStatus expected)
    {
        var manager = new TodoManager();
        _ = manager.AddTask("Ship the parser", parentId: null);

        var result = manager.UpdateTask(1, status);

        Assert.DoesNotContain("Error", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, manager.Tasks[0].Status);
    }

    [Fact]
    public void UpdateTask_FindsSubtasksAcrossHierarchyLevels()
    {
        var manager = new TodoManager();
        _ = manager.AddTask("Ship the parser", parentId: null);
        _ = manager.AddTask("Write the lexer", parentId: 1);

        var result = manager.UpdateTask(2, "completed");

        Assert.DoesNotContain("Error", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TodoTaskStatus.Completed, manager.Tasks[0].SubTasks[0].Status);
        // The parent must not be touched by an update aimed at its child.
        Assert.Equal(TodoTaskStatus.NotStarted, manager.Tasks[0].Status);
    }

    [Fact]
    public void UpdateTask_WithInvalidStatus_IsRejectedAndLeavesStatusUnchanged()
    {
        var manager = new TodoManager();
        _ = manager.AddTask("Ship the parser", parentId: null);
        _ = manager.UpdateTask(1, "in progress");

        var result = manager.UpdateTask(1, "abandoned");

        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TodoTaskStatus.InProgress, manager.Tasks[0].Status);
    }

    [Fact]
    public void UpdateTask_WithUnknownId_IsRejected()
    {
        var manager = new TodoManager();
        _ = manager.AddTask("Ship the parser", parentId: null);

        var result = manager.UpdateTask(99, "completed");

        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TodoTaskStatus.NotStarted, manager.Tasks[0].Status);
    }

    // ---- Requirement 4: add-task-notes ----

    [Fact]
    public void AddTaskNotes_AppendsRatherThanReplacing()
    {
        var manager = new TodoManager();
        _ = manager.AddTask("Ship the parser", parentId: null);

        _ = manager.AddTaskNotes(1, "Blocked on the lexer");
        var result = manager.AddTaskNotes(1, "Unblocked");

        Assert.DoesNotContain("Error", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Blocked on the lexer", "Unblocked"], manager.Tasks[0].Notes);
    }

    [Fact]
    public void AddTaskNotes_FindsSubtasksAcrossHierarchyLevels()
    {
        var manager = new TodoManager();
        _ = manager.AddTask("Ship the parser", parentId: null);
        _ = manager.AddTask("Write the lexer", parentId: 1);

        _ = manager.AddTaskNotes(2, "Handles unicode");

        Assert.Equal(["Handles unicode"], manager.Tasks[0].SubTasks[0].Notes);
        // The note must land on the subtask, not on its parent.
        Assert.Empty(manager.Tasks[0].Notes);
    }

    [Fact]
    public void AddTaskNotes_WithUnknownId_IsRejectedAndAddsNothing()
    {
        var manager = new TodoManager();
        _ = manager.AddTask("Ship the parser", parentId: null);

        var result = manager.AddTaskNotes(99, "Nowhere to go");

        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(manager.Tasks[0].Notes);
    }

    // ---- Requirements 5 and 6: markdown rendering ----

    [Fact]
    public void GetMarkdown_RendersHeaderStatusMarkersNestingAndNumberedNotes()
    {
        var manager = BuildPopulatedManager();

        var markdown = manager.GetMarkdown();

        var expected = string.Join(
            "\n",
            "# TODO",
            "",
            "- [ ] 1. Ship the parser",
            "  1. Blocked on the lexer",
            "  2. Unblocked",
            "  - [x] 2. Write the lexer",
            "    1. Handles unicode",
            "  - [-] 3. Write the grammar",
            "- [~] 4. Old idea"
        );

        Assert.Equal(expected, markdown);
    }

    [Fact]
    public void GetMarkdown_OnAnEmptyList_StillEmitsTheHeader()
    {
        var manager = new TodoManager();

        var markdown = manager.GetMarkdown();

        Assert.StartsWith("# TODO", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ListTasks_ReturnsTheSameMarkdownAsGetMarkdown()
    {
        var manager = BuildPopulatedManager();

        Assert.Equal(manager.GetMarkdown(), manager.ListTasks());
    }

    private static TodoManager BuildPopulatedManager()
    {
        var manager = new TodoManager();
        _ = manager.AddTask("Ship the parser", parentId: null); // 1
        _ = manager.AddTask("Write the lexer", parentId: 1); // 2
        _ = manager.AddTask("Write the grammar", parentId: 1); // 3
        _ = manager.AddTask("Old idea", parentId: null); // 4

        _ = manager.AddTaskNotes(1, "Blocked on the lexer");
        _ = manager.AddTaskNotes(1, "Unblocked");
        _ = manager.AddTaskNotes(2, "Handles unicode");

        _ = manager.UpdateTask(2, "completed");
        _ = manager.UpdateTask(3, "in progress");
        _ = manager.UpdateTask(4, "removed");
        return manager;
    }

    // ---- Registry integration ----

    [Fact]
    public void Provider_RegistersThroughFunctionRegistry()
    {
        var registry = new FunctionRegistry();
        _ = registry.AddProvider(new TodoManager());

        var (contracts, handlers) = registry.Build();

        Assert.Equal(4, contracts.Count());
        Assert.Contains("add-task", handlers.Keys);
        Assert.Contains("add-task-notes", handlers.Keys);
        Assert.Contains("update-task", handlers.Keys);
        Assert.Contains("list-tasks", handlers.Keys);
    }
}
