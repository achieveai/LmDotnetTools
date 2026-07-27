# ADR 0003: Gate host-executed tool calls behind a fail-closed approval decision

* Status: Accepted
* Date: 2026-07-27
* Related issues, PRs, or commits: [#227](https://github.com/achieveai/LmDotnetTools/issues/227)

## Context

When a model requests a tool call that the host executes, there is currently no way to
hold that call for a human or a service policy before it runs. Tool filtering exists, but
`src/LmCore/Middleware/FunctionFilter.cs` applies it at **registry build time** — it
decides which tools exist, not whether a specific invocation with specific arguments may
proceed. There is no call-time policy check anywhere in the pipeline.

The one nearby hook is not usable for this. `ToolCallExecutor` invokes
`IToolResultCallback.OnToolCallStartedAsync` (`src/LmCore/Middleware/ToolCallExecutor.cs:117-125`),
but its return value is discarded — it observes, it cannot veto.

Surveying the execution paths produced the finding that shaped the design: **there are
two independent host tool-execution paths, not one.**

* `src/LmCore/Middleware/ToolCallExecutor.cs` runs a sequential `foreach` and appends
  results in input order.
* `src/LmMultiTurn/MultiTurnAgentLoop.cs:734-745` holds its own handler dictionary and
  dispatches **fully in parallel**, never calling `ToolCallExecutor` at all.

A third site, `FunctionCallMiddleware.cs:628-653`, fans out single-call invocations
concurrently from the streaming path using `CancellationToken.None`, so anything awaited
there is not cancellable by the caller's token.

Two further constraints. Binary and source compatibility is a hard gate: no required
member may be added to an existing public interface and no existing positional record or
constructor may change in place, because precompiled consumers must keep working. And the
delegate signatures `ToolHandler` / `ToolCallResultHandler`
(`src/LmCore/Middleware/ToolHandler.cs:12-28`) are a published contract that cannot change
shape.

Finally, arguments are raw provider text carried as a `string` end to end
(`ToolCall.FunctionArgs`, `src/LmCore/Messages/ToolCall.cs:14`). Nothing in the pipeline
normalizes, sorts, or re-serializes them.

## Decision

A tool invocation is split into a **prepare** phase and an **invoke** phase, implemented
as a reusable component in `LmCore` and wired into **both** execution paths. Preparation —
including awaiting an approval decision — happens concurrently across the calls in a
batch; invocation then proceeds in the existing deterministic order. This preserves the
current result ordering exactly while allowing several approval decisions to be in flight
at once.

The rejected alternative was wrapping handlers where the registry populates its dictionary
(`FunctionRegistry.cs:203`). One change would have covered both paths, but a wrapper sits
*inside* the invocation and so cannot express "prepare everything, then invoke in order" —
it would serialize approvals behind execution order.

**Approval is fail-closed and unanimous-allow.** Execution proceeds only on an explicit
allow from every configured approver. Deny, timeout, overload, a missing approver, a hook
that throws, revocation, and cancellation all block execution. The observable invariant is
that a handler runs **exactly zero or one times** — never twice, and never after a denial.

**Precedence is fixed, and the cheap checks do not open a gate.** The order is: execution
target and handler registration and provider-native policy first; then host execution
policy; then the asynchronous approval gate; then handler invocation. Neither of the first
two opens a gate, so a call that a local policy already refuses never reaches a human or a
remote approver. Each refusal carries a distinct stable code (`provider_policy_denied`,
`host_policy_denied`, `denied`, `timeout`, `overload`, `revoked`, `hook_error`,
`missing_approver`, `cancelled`) so a caller can tell *why* something was blocked without
parsing prose. Denials are returned as error results in the existing shape used for
unknown functions (`ToolCallExecutor.cs:215-248`) rather than as thrown exceptions, so a
denial is an ordinary result the loop already knows how to handle.

**The approved argument bytes are frozen when the gate opens, and those exact bytes are
what execute.** Because nothing in the pipeline canonicalizes JSON, the "canonical" value
is precisely the string that would be handed to the handler, hashed once as SHA-256 over
UTF-8 and rendered as lowercase hex. This is documented on the public type so that nobody
mistakes it for a sorted or normalized JSON form. Freezing at gate-open closes the
time-of-check-to-time-of-use gap: an approver decides on the same bytes that run.

**Waiting is always bounded.** The maximum wait is finite and defaults to five minutes.
The effective expiry is the earliest of that configured maximum, any provider or host
deadline, run or turn cancellation, a provider interrupt, shutdown, and revocation — so a
pending approval can never outlive the run that requested it.

**Compatibility is preserved by addition only.** Approval enters through new trailing
optional parameters and new abstractions; no existing interface gains a required member
and no positional record changes in place. With no policy and no gate configured, behavior
is byte-for-byte identical to the baseline.

**Workflow controller tools are exempt.** `LmWorkflow.WorkflowSession` always runs with no
approval gate. Its tools are internal orchestration steps chosen by the workflow engine,
not model-requested actions against the host, so gating them would deadlock a workflow
behind an approver with nothing meaningful to decide. Tests assert that workflow tools
never request approval.

## Consequences

A host can now interpose a human or a service on any model-requested, host-executed tool
call, with a guarantee that a blocked call did not run and that an approved call ran
exactly the arguments that were approved. The stable code set makes denials
programmatically actionable.

Two costs are accepted. First, the prepare/invoke split is wired into two call sites
rather than one, so a future third execution path must be wired deliberately — that is the
direct consequence of the codebase having two independent paths, and the alternative
single-site change could not meet the ordering requirement. Second, an approval gate
introduces latency proportional to approver response time; concurrent preparation bounds
this to roughly the slowest single decision per batch rather than their sum.

The `CancellationToken.None` on the streaming fan-out path
(`FunctionCallMiddleware.cs:628-653`) is a pre-existing defect that becomes materially
worse once a gate can be awaited there — a non-cancellable wait would outlive its caller.
It is fixed as part of wiring that path.

This ADR covers approval of tools the host executes through a delegate. Tools executed
entirely by a provider or a CLI, which never call a host delegate, cannot be gated here
and are out of scope.
