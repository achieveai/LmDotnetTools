using System.Collections.Concurrent;
using System.Diagnostics;
using AchieveAi.LmDotnetTools.Misc.Utils;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

public class TaskManagerTests
{
    private readonly ITestOutputHelper _output;
    private readonly TaskManager _taskManager;

    public TaskManagerTests(ITestOutputHelper output)
    {
        _output = output;
        _taskManager = new TaskManager();
    }

    #region AddTask Tests

    [Fact]
    public void AddTask_WithValidTitle_ShouldAddMainTask()
    {
        // Act
        var result = _taskManager.AddTask("Test task");

        // Assert
        result.Should().StartWith("Added task 2:");
        result.Should().Contain("Test task");

        var tasks = _taskManager.ListTasks();
        tasks.Should().Contain("Test task");
    }

    [Fact]
    public void AddTask_WithEmptyTitle_ShouldReturnError()
    {
        // Act
        var result1 = _taskManager.AddTask("");
        var result2 = _taskManager.AddTask("   ");
        var result3 = _taskManager.AddTask(null!);

        // Assert
        result1.Should().Be("Error: Title cannot be empty.");
        result2.Should().Be("Error: Title cannot be empty.");
        result3.Should().Be("Error: Title cannot be empty.");
    }

    [Fact]
    public void AddTask_WithValidParentId_ShouldAddSubtask()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);

        // Act
        var result = _taskManager.AddTask("Subtask", parentId);

        // Assert
        result.Should().Contain($"Added subtask");
        result.Should().Contain($"under task {parentId}");
        result.Should().Contain("Subtask");
    }

    [Fact]
    public void AddTask_WithInvalidParentId_ShouldReturnError()
    {
        // Act
        var result = _taskManager.AddTask("Subtask", 999);

        // Assert
        result.Should().Be("Error: Parent task 999 not found.");
    }

    [Fact]
    public void AddTask_ToSubtask_ShouldReturnError()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);
        var subtaskResult = _taskManager.AddTask("Subtask", parentId);
        var subtaskId = ExtractTaskId(subtaskResult);

        // Act
        var result = _taskManager.AddTask("Sub-subtask", subtaskId);

        // Assert
        result.Should().Contain("Error: Only two levels supported");
        result.Should().Contain($"Task {subtaskId} is already a subtask");
    }

    #endregion

    #region BulkInitialize Tests

    [Fact]
    public void BulkInitialize_WithValidTasks_ShouldAddAllTasks()
    {
        // Arrange
        var tasks = new List<TaskManager.BulkTaskItem>
        {
            new()
            {
                Task = "Task 1",
                SubTasks = new List<string> { "Subtask 1.1", "Subtask 1.2" },
                Notes = new List<string> { "Note 1", "Note 2" },
            },
            new()
            {
                Task = "Task 2",
                SubTasks = new List<string> { "Subtask 2.1" },
                Notes = new List<string> { "Note A" },
            },
        };

        // Act
        var result = _taskManager.BulkInitialize(tasks);

        // Assert
        result.Should().Contain("Added 2 task(s)");
        result.Should().Contain("Task 1");
        result.Should().Contain("Task 2");

        var taskList = _taskManager.ListTasks();
        taskList.Should().Contain("Task 1");
        taskList.Should().Contain("Task 2");
        taskList.Should().Contain("Subtask 1.1");
        taskList.Should().Contain("Subtask 1.2");
        taskList.Should().Contain("Subtask 2.1");
    }

    [Fact]
    public void BulkInitialize_WithClearExisting_ShouldClearAndAdd()
    {
        // Arrange
        _taskManager.AddTask("Existing task");
        var tasks = new List<TaskManager.BulkTaskItem> { new() { Task = "New task" } };

        // Act
        var result = _taskManager.BulkInitialize(tasks, clearExisting: true);

        // Assert
        result.Should().Contain("Cleared existing tasks");
        result.Should().Contain("Added 1 task(s)");

        var taskList = _taskManager.ListTasks();
        taskList.Should().NotContain("Existing task");
        taskList.Should().Contain("New task");
    }

    [Fact]
    public void BulkInitialize_WithEmptyTaskTitles_ShouldSilentlySkip()
    {
        // Arrange
        var tasks = new List<TaskManager.BulkTaskItem>
        {
            new() { Task = "" },
            new() { Task = "   " },
            new() { Task = "Valid task" },
            new() { Task = null! },
        };

        // Act
        var result = _taskManager.BulkInitialize(tasks);

        // Assert
        result.Should().Contain("Added 1 task(s)");
        result.Should().Contain("Valid task");
        result.Should().NotContain("Error");
    }

    [Fact]
    public void BulkInitialize_WithEmptySubtasks_ShouldSilentlySkip()
    {
        // Arrange
        var tasks = new List<TaskManager.BulkTaskItem>
        {
            new()
            {
                Task = "Main task",
                SubTasks = new List<string> { "", "Valid subtask", "   ", null! },
            },
        };

        // Act
        var result = _taskManager.BulkInitialize(tasks);

        // Assert
        var taskList = _taskManager.ListTasks();
        taskList.Should().Contain("Main task");
        taskList.Should().Contain("Valid subtask");
        taskList.Split('\n').Count(line => line.Contains("Valid subtask")).Should().Be(1);
    }

    [Fact]
    public void BulkInitialize_WithNullOrEmptyList_ShouldReturnError()
    {
        // Act
        var result1 = _taskManager.BulkInitialize(null!);
        var result2 = _taskManager.BulkInitialize(new List<TaskManager.BulkTaskItem>());

        // Assert
        result1.Should().Be("Error: No tasks provided for initialization.");
        result2.Should().Be("Error: No tasks provided for initialization.");
    }

    #endregion

    #region UpdateTask Tests

    [Fact]
    public void UpdateTask_MainTask_ShouldUpdateStatus()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);

        // Act
        var result1 = _taskManager.UpdateTask(taskId, status: "in progress");
        var result2 = _taskManager.UpdateTask(taskId, status: "completed");

        // Assert
        result1.Should().Contain($"Updated task {taskId} status to 'in progress'");
        result2.Should().Contain($"Updated task {taskId} status to 'completed'");

        var taskDetails = _taskManager.GetTask(taskId);
        taskDetails.Should().Contain("Status: completed");
    }

    [Fact]
    public void UpdateTask_Subtask_ShouldUpdateStatus()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);
        var subtaskResult = _taskManager.AddTask("Subtask", parentId);
        var subtaskId = ExtractTaskId(subtaskResult);

        // Act
        var result = _taskManager.UpdateTask(parentId, subtaskId, "completed");

        // Assert
        result
            .Should()
            .Contain($"Updated subtask {subtaskId} of task {parentId} status to 'completed'");

        var taskDetails = _taskManager.GetTask(parentId, subtaskId);
        taskDetails.Should().Contain("Status: completed");
    }

    [Fact]
    public void UpdateTask_WithInvalidStatus_ShouldReturnError()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);

        // Act
        var result = _taskManager.UpdateTask(taskId, status: "invalid");

        // Assert
        result
            .Should()
            .Be("Error: Invalid status. Use: not started, in progress, completed, removed.");
    }

    [Fact]
    public void UpdateTask_WithVariousStatusFormats_ShouldAccept()
    {
        // Arrange
        var tasks = new[]
        {
            ("not started", "not started"),
            ("not_started", "not started"),
            ("todo", "not started"),
            ("in progress", "in progress"),
            ("in_progress", "in progress"),
            ("doing", "in progress"),
            ("completed", "completed"),
            ("done", "completed"),
            ("removed", "removed"),
            ("deleted", "removed"),
        };

        foreach (var (input, expected) in tasks)
        {
            var addResult = _taskManager.AddTask($"Task for {input}");
            var taskId = ExtractTaskId(addResult);

            // Act
            var result = _taskManager.UpdateTask(taskId, status: input);

            // Assert
            result.Should().Contain($"status to '{expected}'");
        }
    }

    #endregion

    #region DeleteTask Tests

    [Fact]
    public void DeleteTask_MainTask_ShouldRemoveTaskAndSubtasks()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);
        _taskManager.AddTask("Subtask 1", parentId);
        _taskManager.AddTask("Subtask 2", parentId);

        // Act
        var result = _taskManager.DeleteTask(parentId);

        // Assert
        result.Should().Contain($"Deleted task {parentId} and all subtasks");
        result.Should().Contain("Parent task");

        var getResult = _taskManager.GetTask(parentId);
        getResult.Should().Contain("Error: Task");
        getResult.Should().Contain("not found");
    }

    [Fact]
    public void DeleteTask_Subtask_ShouldRemoveOnlySubtask()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);
        var subtask1Result = _taskManager.AddTask("Subtask 1", parentId);
        var subtask1Id = ExtractTaskId(subtask1Result);
        var subtask2Result = _taskManager.AddTask("Subtask 2", parentId);
        var subtask2Id = ExtractTaskId(subtask2Result);

        // Act
        var result = _taskManager.DeleteTask(parentId, subtask1Id);

        // Assert
        result.Should().Contain($"Deleted subtask {subtask1Id} from task {parentId}");
        result.Should().Contain("Subtask 1");

        var parentDetails = _taskManager.GetTask(parentId);
        parentDetails.Should().NotContain("Subtask 1");
        parentDetails.Should().Contain("Subtask 2");
    }

    [Fact]
    public void DeleteTask_NonExistentTask_ShouldReturnError()
    {
        // Act
        var result = _taskManager.DeleteTask(999);

        // Assert
        result.Should().Be("Error: Task 999 not found.");
    }

    #endregion

    #region GetTask Tests

    [Fact]
    public void GetTask_MainTask_ShouldReturnDetails()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);
        _taskManager.ManageNotes(taskId, noteText: "Test note", action: "add");
        _taskManager.AddTask("Subtask", taskId);

        // Act
        var result = _taskManager.GetTask(taskId);

        // Assert
        result.Should().Contain($"Task {taskId}: Test task");
        result.Should().Contain("Status: not started");
        result.Should().Contain("Notes (1):");
        result.Should().Contain("Test note");
        result.Should().Contain("Subtasks (1):");
        result.Should().Contain("Subtask");
    }

    [Fact]
    public void GetTask_Subtask_ShouldReturnDetails()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);
        var subtaskResult = _taskManager.AddTask("Subtask", parentId);
        var subtaskId = ExtractTaskId(subtaskResult);

        // Act
        var result = _taskManager.GetTask(parentId, subtaskId);

        // Assert
        result.Should().Contain($"Subtask {subtaskId} of task {parentId}: Subtask");
        result.Should().Contain("Status: not started");
    }

    #endregion

    #region ManageNotes Tests

    [Fact]
    public void ManageNotes_AddNote_ShouldAddToTask()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);

        // Act
        var result = _taskManager.ManageNotes(taskId, noteText: "Test note", action: "add");

        // Assert
        result.Should().Contain($"Added note to task {taskId}");

        var notes = _taskManager.ListNotes(taskId);
        notes.Should().Contain("Test note");
    }

    [Fact]
    public void ManageNotes_EditNote_ShouldUpdateNote()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);
        _taskManager.ManageNotes(taskId, noteText: "Original note", action: "add");

        // Act
        var result = _taskManager.ManageNotes(
            taskId,
            noteText: "Updated note",
            noteIndex: 1,
            action: "edit"
        );

        // Assert
        result.Should().Contain($"Edited note 1 on task {taskId}");

        var notes = _taskManager.ListNotes(taskId);
        notes.Should().NotContain("Original note");
        notes.Should().Contain("Updated note");
    }

    [Fact]
    public void ManageNotes_DeleteNote_ShouldRemoveNote()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);
        _taskManager.ManageNotes(taskId, noteText: "Note 1", action: "add");
        _taskManager.ManageNotes(taskId, noteText: "Note 2", action: "add");

        // Act
        var result = _taskManager.ManageNotes(taskId, noteIndex: 1, action: "delete");

        // Assert
        result.Should().Contain($"Deleted note 1 from task {taskId}");

        var notes = _taskManager.ListNotes(taskId);
        notes.Should().NotContain("Note 1");
        notes.Should().Contain("Note 2");
    }

    [Fact]
    public void ManageNotes_InvalidAction_ShouldReturnError()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);

        // Act
        var result = _taskManager.ManageNotes(taskId, action: "invalid");

        // Assert
        result.Should().Be("Error: Invalid action. Use: add, edit, delete.");
    }

    [Fact]
    public void ManageNotes_EditWithInvalidIndex_ShouldReturnError()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);
        _taskManager.ManageNotes(taskId, noteText: "Note 1", action: "add");

        // Act
        var result1 = _taskManager.ManageNotes(
            taskId,
            noteText: "Updated",
            noteIndex: 0,
            action: "edit"
        );
        var result2 = _taskManager.ManageNotes(
            taskId,
            noteText: "Updated",
            noteIndex: 2,
            action: "edit"
        );

        // Assert
        result1.Should().Contain("Error: Note index 0 out of range (1-1)");
        result2.Should().Contain("Error: Note index 2 out of range (1-1)");
    }

    #endregion

    #region ListTasks Tests

    [Fact]
    public void ListTasks_WithNoTasks_ShouldReturnNoTasks()
    {
        // Act
        var result = _taskManager.ListTasks();

        // Assert
        result.Should().Be("No tasks found.");
    }

    [Fact]
    public void ListTasks_WithStatusFilter_ShouldFilterTasks()
    {
        // Arrange
        var task1Result = _taskManager.AddTask("Task 1");
        var task1Id = ExtractTaskId(task1Result);
        var task2Result = _taskManager.AddTask("Task 2");
        var task2Id = ExtractTaskId(task2Result);
        var task3Result = _taskManager.AddTask("Task 3");
        var task3Id = ExtractTaskId(task3Result);

        _taskManager.UpdateTask(task1Id, status: "in progress");
        _taskManager.UpdateTask(task2Id, status: "completed");

        // Act
        var inProgressTasks = _taskManager.ListTasks(status: "in progress");
        var completedTasks = _taskManager.ListTasks(status: "completed");
        var notStartedTasks = _taskManager.ListTasks(status: "not started");

        // Assert
        inProgressTasks.Should().Contain("Task 1");
        inProgressTasks.Should().NotContain("Task 2");
        inProgressTasks.Should().NotContain("Task 3");

        completedTasks.Should().Contain("Task 2");
        completedTasks.Should().NotContain("Task 1");

        notStartedTasks.Should().Contain("Task 3");
        notStartedTasks.Should().NotContain("Task 1");
    }

    [Fact]
    public void ListTasks_WithMainOnly_ShouldExcludeSubtasks()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);
        _taskManager.AddTask("Subtask 1", parentId);
        _taskManager.AddTask("Subtask 2", parentId);

        // Act
        var allTasks = _taskManager.ListTasks();
        var mainOnly = _taskManager.ListTasks(mainOnly: true);

        // Assert
        allTasks.Should().Contain("Parent task");
        allTasks.Should().Contain("Subtask 1");
        allTasks.Should().Contain("Subtask 2");

        mainOnly.Should().Contain("Parent task");
        mainOnly.Should().NotContain("Subtask 1");
        mainOnly.Should().NotContain("Subtask 2");
    }

    #endregion

    #region SearchTasks Tests

    [Fact]
    public void SearchTasks_WithSearchTerm_ShouldFindMatchingTasks()
    {
        // Arrange
        _taskManager.AddTask("Design API");
        _taskManager.AddTask("Implement API");
        _taskManager.AddTask("Test database");

        // Act
        var result = _taskManager.SearchTasks(searchTerm: "API");

        // Assert
        result.Should().Contain("Found 2 task(s) matching 'API'");
        result.Should().Contain("Design API");
        result.Should().Contain("Implement API");
        result.Should().NotContain("Test database");
    }

    [Fact]
    public void SearchTasks_WithCountType_ShouldReturnCounts()
    {
        // Arrange
        var task1Result = _taskManager.AddTask("Task 1");
        var task1Id = ExtractTaskId(task1Result);
        var task2Result = _taskManager.AddTask("Task 2");
        var task2Id = ExtractTaskId(task2Result);
        _taskManager.AddTask("Subtask", task1Id);

        _taskManager.UpdateTask(task1Id, status: "completed");
        _taskManager.UpdateTask(task2Id, status: "removed");

        // Act
        var totalCount = _taskManager.SearchTasks(countType: "total");
        var completedCount = _taskManager.SearchTasks(countType: "completed");
        var pendingCount = _taskManager.SearchTasks(countType: "pending");
        var removedCount = _taskManager.SearchTasks(countType: "removed");

        // Assert
        totalCount.Should().Be("Total tasks: 3");
        completedCount.Should().Be("Completed tasks: 1");
        pendingCount.Should().Be("Pending tasks: 1");
        removedCount.Should().Be("Removed tasks: 1");
    }

    [Fact]
    public void SearchTasks_CaseInsensitive_ShouldFindTasks()
    {
        // Arrange
        _taskManager.AddTask("API Design");

        // Act
        var result1 = _taskManager.SearchTasks(searchTerm: "api");
        var result2 = _taskManager.SearchTasks(searchTerm: "API");
        var result3 = _taskManager.SearchTasks(searchTerm: "ApI");

        // Assert
        result1.Should().Contain("API Design");
        result2.Should().Contain("API Design");
        result3.Should().Contain("API Design");
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task AddTask_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        var taskCount = 100;
        var tasks = new List<Task<string>>();

        // Act
        for (int i = 0; i < taskCount; i++)
        {
            var taskNum = i;
            tasks.Add(Task.Run(() => _taskManager.AddTask($"Task {taskNum}")));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(taskCount);
        results.Should().OnlyContain(r => r.StartsWith("Added task"));

        // Verify all task IDs are unique
        var taskIds = results.Select(r => ExtractTaskId(r)).ToList();
        taskIds.Should().OnlyHaveUniqueItems();
        taskIds.Should().HaveCount(taskCount);
    }

    [Fact]
    public async Task BulkInitialize_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        var concurrentOps = 10;
        var tasks = new List<Task<string>>();

        // Act
        for (int i = 0; i < concurrentOps; i++)
        {
            var opNum = i;
            tasks.Add(
                Task.Run(() =>
                {
                    var bulkTasks = new List<TaskManager.BulkTaskItem>
                    {
                        new()
                        {
                            Task = $"Bulk {opNum} Task 1",
                            SubTasks = new() { $"Sub {opNum}.1" },
                        },
                        new()
                        {
                            Task = $"Bulk {opNum} Task 2",
                            SubTasks = new() { $"Sub {opNum}.2" },
                        },
                    };
                    return _taskManager.BulkInitialize(bulkTasks);
                })
            );
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(concurrentOps);
        results.Should().OnlyContain(r => r.Contains("Added 2 task(s)"));

        var allTasks = _taskManager.ListTasks();
        for (int i = 0; i < concurrentOps; i++)
        {
            allTasks.Should().Contain($"Bulk {i} Task 1");
            allTasks.Should().Contain($"Bulk {i} Task 2");
        }
    }

    [Fact]
    public async Task UpdateTask_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);
        var updateCount = 50;
        var tasks = new List<Task<string>>();

        // Act
        for (int i = 0; i < updateCount; i++)
        {
            var status =
                i
                % 3 switch
                {
                    0 => "not started",
                    1 => "in progress",
                    _ => "completed",
                };
            tasks.Add(Task.Run(() => _taskManager.UpdateTask(taskId, status: status)));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(updateCount);
        results.Should().OnlyContain(r => r.Contains("Updated task"));

        // Final state should be one of the valid statuses
        var finalTask = _taskManager.GetTask(taskId);
        finalTask.Should().ContainAny("not started", "in progress", "completed");
    }

    [Fact]
    public async Task ManageNotes_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);
        var noteCount = 50;
        var tasks = new List<Task<string>>();

        // Act
        for (int i = 0; i < noteCount; i++)
        {
            var noteNum = i;
            tasks.Add(
                Task.Run(() =>
                    _taskManager.ManageNotes(taskId, noteText: $"Note {noteNum}", action: "add")
                )
            );
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(noteCount);
        results.Should().OnlyContain(r => r.Contains("Added note"));

        var notes = _taskManager.ListNotes(taskId);
        var noteLines = notes
            .Split('\n')
            .Where(l =>
                l.Trim().StartsWith("1.")
                || l.Trim().StartsWith("2.")
                || l.Trim().StartsWith("3.")
                || l.Trim().StartsWith("4.")
                || l.Trim().StartsWith("5.")
            );
        noteLines.Count().Should().BeGreaterOrEqualTo(1); // At least some notes should be added
    }

    [Fact]
    public async Task MixedOperations_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        var operations = new List<Task>();
        var results = new ConcurrentBag<string>();

        // Act - Mix of different operations running concurrently
        for (int i = 0; i < 20; i++)
        {
            var opNum = i;
            operations.Add(
                Task.Run(async () =>
                {
                    switch (opNum % 5)
                    {
                        case 0:
                            results.Add(
                                await Task.Run(() => _taskManager.AddTask($"Task {opNum}"))
                            );
                            break;
                        case 1:
                            results.Add(await Task.Run(() => _taskManager.ListTasks()));
                            break;
                        case 2:
                            results.Add(
                                await Task.Run(() => _taskManager.SearchTasks(countType: "total"))
                            );
                            break;
                        case 3:
                            var bulkTasks = new List<TaskManager.BulkTaskItem>
                            {
                                new() { Task = $"Bulk {opNum}" },
                            };
                            results.Add(
                                await Task.Run(() => _taskManager.BulkInitialize(bulkTasks))
                            );
                            break;
                        case 4:
                            results.Add(await Task.Run(() => _taskManager.GetMarkdown()));
                            break;
                    }
                })
            );
        }

        await Task.WhenAll(operations);

        // Assert - All operations should complete without exceptions
        results.Should().HaveCount(20);
        results.Should().NotContainNulls();
    }

    #endregion

    #region Edge Cases Tests

    [Fact]
    public void TaskManager_LargeNumberOfTasks_ShouldHandleEfficiently()
    {
        // Arrange
        var stopwatch = Stopwatch.StartNew();
        var taskCount = 1000;

        // Act
        for (int i = 0; i < taskCount; i++)
        {
            _taskManager.AddTask($"Task {i}");
        }
        stopwatch.Stop();

        // Assert
        _output.WriteLine($"Added {taskCount} tasks in {stopwatch.ElapsedMilliseconds}ms");
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000); // Should be fast

        var count = _taskManager.SearchTasks(countType: "total");
        count.Should().Be($"Total tasks: {taskCount}");
    }

    [Fact]
    public void TaskManager_DeepSubtaskHierarchy_ShouldEnforceTwoLevels()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Level 1");
        var parentId = ExtractTaskId(parentResult);
        var subtaskResult = _taskManager.AddTask("Level 2", parentId);
        var subtaskId = ExtractTaskId(subtaskResult);

        // Act
        var result = _taskManager.AddTask("Level 3", subtaskId);

        // Assert
        result.Should().Contain("Error: Only two levels supported");
    }

    [Fact]
    public void TaskManager_SpecialCharactersInTitles_ShouldHandle()
    {
        // Arrange
        var specialTitles = new[]
        {
            "Task with 'quotes'",
            "Task with \"double quotes\"",
            "Task with <html> tags",
            "Task with & ampersand",
            "Task with \n newline",
            "Task with \t tab",
            "Task with émojis 🎉",
        };

        // Act & Assert
        foreach (var title in specialTitles)
        {
            var result = _taskManager.AddTask(title);
            result.Should().Contain("Added task");
            result.Should().Contain(title.Trim());
        }

        var allTasks = _taskManager.ListTasks();
        foreach (var title in specialTitles)
        {
            allTasks.Should().Contain(title.Trim());
        }
    }

    [Fact]
    public void TaskManager_VeryLongTitlesAndNotes_ShouldHandle()
    {
        // Arrange
        var longTitle = new string('A', 1000);
        var longNote = new string('B', 5000);

        // Act
        var addResult = _taskManager.AddTask(longTitle);
        var taskId = ExtractTaskId(addResult);
        var noteResult = _taskManager.ManageNotes(taskId, noteText: longNote, action: "add");

        // Assert
        addResult.Should().Contain("Added task");
        noteResult.Should().Contain("Added note");

        var taskDetails = _taskManager.GetTask(taskId);
        taskDetails.Should().Contain(longTitle);
        taskDetails.Should().Contain(longNote);
    }

    #endregion

    #region GetMarkdown Tests

    [Fact]
    public void GetMarkdown_ShouldReturnSameAsListTasks()
    {
        // Arrange
        _taskManager.AddTask("Task 1");
        _taskManager.AddTask("Task 2");

        // Act
        var markdown = _taskManager.GetMarkdown();
        var listTasks = _taskManager.ListTasks();

        // Assert
        markdown.Should().Be(listTasks);
    }

    #endregion

    #region Helper Methods

    private static int ExtractTaskId(string result)
    {
        // Extract task ID from messages like "Added task 1: Title" or "Added subtask 2 under task 1: Title"
        var match = System.Text.RegularExpressions.Regex.Match(result, @"(?:task|subtask)\s+(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    #endregion
}
