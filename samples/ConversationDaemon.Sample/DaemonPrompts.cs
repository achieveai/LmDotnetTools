namespace ConversationDaemon.Sample;

/// <summary>
/// Verbatim mock instruction-chain prompts driven by the scripted flow. Each string is copied
/// exactly from <c>samples/LmStreaming.Sample/PromptExamples.md</c> so the mock provider
/// (<c>test-anthropic</c>) parses the intended chain. Raw string literals keep the embedded JSON —
/// including the nested chain's literal <c>\"</c> escapes — byte-for-byte identical to the source.
/// </summary>
internal static class DaemonPrompts
{
    /// <summary>Warm-up: a single <c>calculate</c> tool call (PromptExamples.md → "Calculate Tool").</summary>
    public const string WarmUp =
        """<|instruction_start|>{"instruction_chain":[{"id":"calc-add","id_message":"Adding numbers","messages":[{"tool_call":[{"name":"calculate","args":{"a":10,"operation":"add","b":5}}]}]}]}<|instruction_end|>""";

    /// <summary>
    /// Sub-agent delegation: the parent calls the <c>Agent</c> tool whose <c>prompt</c> is itself a
    /// complete instruction chain; the sub-agent runs <c>calculate</c> then replies "hi from agent",
    /// and the parent wraps up (PromptExamples.md → "Sub-Agent Delegation (Nested Instruction Chains)").
    /// </summary>
    public const string SubAgentDelegation =
        """<|instruction_start|>{"instruction_chain":[{"id":"parent","id_message":"Delegate to sub-agent","messages":[{"tool_call":[{"name":"Agent","args":{"subagent_type":"general-purpose","prompt":"<|instruction_start|>{\"instruction_chain\":[{\"id\":\"sub-tool\",\"messages\":[{\"tool_call\":[{\"name\":\"calculate\",\"args\":{\"a\":2,\"operation\":\"add\",\"b\":3}}]}]},{\"id\":\"sub-text\",\"messages\":[{\"text\":\"hi from agent\"}]}]}<|instruction_end|>"}}]}]},{"id":"parent2","id_message":"Wrap up","messages":[{"text":"Parent done: sub-agent finished."}]}]}<|instruction_end|>""";

    /// <summary>
    /// Wait / park-and-wake: the run parks on a 3s <c>timer</c> (returning a deferred result) and
    /// auto-resumes with a text message once it fires (PromptExamples.md → "Wait / Trigger (Park-and-Wake)").
    /// </summary>
    public const string WaitParkAndWake =
        """<|instruction_start|>{"instruction_chain":[{"id":"arm-wait","id_message":"Arming a 3s timer","messages":[{"tool_call":[{"name":"Wait","args":{"kind":"timer","args":{"delay":"3s"},"timeout":"30s","label":"demo-timer"}}]}]},{"id":"after-wait","id_message":"Resumed after wait","messages":[{"text":"Timer fired — the run resumed automatically after the wait."}]}]}<|instruction_end|>""";
}
