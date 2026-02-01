using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.Agents;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

[ApiController]
[Route("api/conversations")]
public class ConversationsController(
    IConversationStore store,
    MultiTurnAgentPool agentPool,
    IChatModeStore modeStore) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var threads = await store.ListThreadsAsync(limit, offset, ct);
        var result = threads.Select(t => new ConversationSummary
        {
            ThreadId = t.ThreadId,
            Title = t.Properties?.TryGetValue("title", out var titleObj) == true
                ? titleObj?.ToString() ?? "New Conversation"
                : "New Conversation",
            Preview = t.Properties?.TryGetValue("preview", out var previewObj) == true
                ? previewObj?.ToString()
                : null,
            LastUpdated = t.LastUpdated,
        });
        return Ok(result);
    }

    [HttpGet("{threadId}/messages")]
    public async Task<IActionResult> GetMessages(
        string threadId,
        CancellationToken ct = default)
    {
        var messages = await store.LoadMessagesAsync(threadId, ct);
        return Ok(messages);
    }

    [HttpPut("{threadId}/metadata")]
    public async Task<IActionResult> UpdateMetadata(
        string threadId,
        [FromBody] ConversationMetadataUpdate update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var existing = await store.LoadMetadataAsync(threadId, ct);
        var propertiesBuilder = existing?.Properties?.ToBuilder()
            ?? ImmutableDictionary.CreateBuilder<string, object>();

        if (update.Title != null)
        {
            propertiesBuilder["title"] = update.Title;
        }

        if (update.Preview != null)
        {
            propertiesBuilder["preview"] = update.Preview;
        }

        var metadata = new ThreadMetadata
        {
            ThreadId = threadId,
            CurrentRunId = existing?.CurrentRunId,
            LatestRunId = existing?.LatestRunId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionMappings = existing?.SessionMappings,
            Properties = propertiesBuilder.ToImmutable(),
        };

        await store.SaveMetadataAsync(threadId, metadata, ct);
        return Ok();
    }

    [HttpDelete("{threadId}")]
    public async Task<IActionResult> Delete(
        string threadId,
        CancellationToken ct = default)
    {
        await agentPool.RemoveAgentAsync(threadId);
        await store.DeleteThreadAsync(threadId, ct);
        return NoContent();
    }

    [HttpPost("{threadId}/mode")]
    public async Task<IActionResult> SwitchMode(
        string threadId,
        [FromBody] SwitchModeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mode = await modeStore.GetModeAsync(request.ModeId, ct);
        if (mode == null)
        {
            return NotFound(new { error = $"Mode '{request.ModeId}' not found." });
        }

        _ = await agentPool.RecreateAgentWithModeAsync(threadId, mode);
        return Ok(new { modeId = mode.Id, modeName = mode.Name });
    }
}
