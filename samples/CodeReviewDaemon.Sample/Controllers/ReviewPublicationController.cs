using System.Text.Json;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Security;
using Microsoft.AspNetCore.Mvc;

namespace CodeReviewDaemon.Sample.Controllers;

[ApiController]
[Route("api/review-publication/rounds/{roundId:long}/actions/{operation}")]
[ReviewBridgeAuth]
public sealed class ReviewPublicationController(IServiceProvider services) : ControllerBase
{
    private IReviewPublicationOperations Operations => services.GetRequiredService<IReviewPublicationOperations>();

    [HttpPost]
    public async Task<IActionResult> PublishAsync(
        long roundId,
        string operation,
        [FromBody] JsonElement body,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var outcome = operation switch
            {
                "CreateRootSummary" => await InvokeAsync<RootSummaryRequest>(
                    roundId,
                    body,
                    Operations.CreateRootSummaryAsync,
                    cancellationToken
                ),
                "AppendSummaryDelta" => await InvokeAsync<SummaryDeltaRequest>(
                    roundId,
                    body,
                    Operations.AppendSummaryDeltaAsync,
                    cancellationToken
                ),
                "SubmitInlineFindings" => await InvokeAsync<InlineFindingsRequest>(
                    roundId,
                    body,
                    Operations.SubmitInlineFindingsAsync,
                    cancellationToken
                ),
                "PostClarificationQuestion" => await InvokeAsync<ClarificationQuestionRequest>(
                    roundId,
                    body,
                    Operations.PostClarificationQuestionAsync,
                    cancellationToken
                ),
                "ReplyToDiscussion" => await InvokeAsync<DiscussionReplyRequest>(
                    roundId,
                    body,
                    Operations.ReplyToDiscussionAsync,
                    cancellationToken
                ),
                "FinalizeRound" => await InvokeAsync<FinalizeRoundRequest>(
                    roundId,
                    body,
                    Operations.FinalizeRoundAsync,
                    cancellationToken
                ),
                _ => Rejected(string.Empty, "unsupported_operation"),
            };

            return outcome.RejectionCode switch
            {
                null => Ok(outcome),
                "round_id_mismatch" or "action_id_conflict" or "round_not_running" => Conflict(outcome),
                _ => UnprocessableEntity(outcome),
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return UnprocessableEntity(Rejected(string.Empty, "invalid_request"));
        }
    }

    private static async Task<PublicationOutcome> InvokeAsync<TRequest>(
        long routeRoundId,
        JsonElement body,
        Func<TRequest, CancellationToken, Task<PublicationOutcome>> invoke,
        CancellationToken cancellationToken
    )
        where TRequest : class
    {
        var request = body.Deserialize<TRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (request is null)
        {
            return Rejected(string.Empty, "invalid_request");
        }

        var scope = request switch
        {
            RootSummaryRequest value => value.Scope,
            SummaryDeltaRequest value => value.Scope,
            InlineFindingsRequest value => value.Scope,
            ClarificationQuestionRequest value => value.Scope,
            DiscussionReplyRequest value => value.Scope,
            FinalizeRoundRequest value => value.Scope,
            _ => null,
        };
        if (scope is null)
        {
            return Rejected(string.Empty, "invalid_request");
        }

        if (scope.RoundId != routeRoundId)
        {
            return Rejected(scope.ActionId, "round_id_mismatch");
        }

        return await invoke(request, cancellationToken).ConfigureAwait(false);
    }

    private static PublicationOutcome Rejected(string actionId, string code) =>
        new(
            CodeReviewDaemon.Sample.Persistence.Models.ReviewActionStatus.Rejected,
            actionId,
            null,
            null,
            null,
            null,
            false,
            null,
            code
        );
}
