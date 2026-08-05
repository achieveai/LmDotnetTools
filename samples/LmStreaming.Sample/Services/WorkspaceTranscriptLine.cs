using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.Persistence;

// The envelope has its own `Role` property, so the enum needs an alias to stay addressable inside
// this type — an unaliased `Role.None` binds to the property and does not compile.
using MessageRole = AchieveAi.LmDotnetTools.LmCore.Messages.Role;

namespace LmStreaming.Sample.Services;

/// <summary>
///     One line of a workspace transcript file (<c>.conversations/*.jsonl</c>) — the envelope, its
///     <c>uid</c> derivation, the file-leaf slug, and the exact bytes each line serializes to.
/// </summary>
/// <remarks>
///     <para>
///     Everything here is PURE: no I/O, no async, no clock, no DI. Given the same
///     <see cref="PersistedMessage"/> this type produces the same bytes in every process, on every
///     flush, forever — which is what makes <c>uid</c> usable as an append watermark and as the
///     de-duplication key a reader collapses on.
///     </para>
///     <para>
///     <b><c>message_json</c> is an OPAQUE STRING, always.</b> It carries
///     <see cref="PersistedMessage.MessageJson"/> verbatim as a JSON <i>string</i> value, never as an
///     inlined object. A reader that wants the message parses that string itself. This is deliberate:
///     an object-or-string union is exactly the shape that gets silently dropped by a tolerant JSONL
///     reader (<c>read_ndjson_objects</c> with <c>ignore_errors=true</c>), and inlining would let a
///     provider's arbitrary message shape widen the transcript's own schema.
///     </para>
///     <para>
///     <b>Casing asymmetry, on purpose.</b> The envelope's <c>role</c> is PascalCase (<c>"User"</c>) —
///     it passes through <see cref="PersistedMessage.Role"/>, which is written as
///     <c>message.Role.ToString()</c>. The same role <i>inside</i> <c>message_json</c> is lowercase
///     (<c>"user"</c>), because <see cref="MessageRole"/> carries <c>JsonPropertyName</c> attributes.
///     Readers must not assume one casing across both layers.
///     </para>
///     <para>
///     <b>Known gap, stated where a reader will see it:</b> a deferred tool result (a late client-tool
///     or <c>AskUserQuestion</c> answer) is written back through <c>ReplaceMessageAsync</c>, which
///     mutates the stored row IN PLACE — same <see cref="PersistedMessage.Id"/>, same
///     <see cref="PersistedMessage.Timestamp"/>, therefore the same <c>uid</c>. The append watermark has
///     already passed that <c>uid</c>, so the transcript keeps the question and never gets the answer.
///     Re-emitting under the same <c>uid</c> was rejected: it would break the "distinct uid count ==
///     line count" guarantee.
///     </para>
/// </remarks>
public sealed record WorkspaceTranscriptLine
{
    /// <summary>
    ///     Schema version stamped on every line. <b>Bump rule:</b> bump when a field is REMOVED or
    ///     RETYPED. Adding a new nullable field does NOT bump — a reader that ignores unknown keys keeps
    ///     working, which is the whole point of versioning per line rather than per file.
    /// </summary>
    /// <remarks>
    ///     <b>Still 1 after the state line was deleted, and that is the rule applied rather than an
    ///     exception to it.</b> A <c>state</c> line type once existed here with its own <c>key</c> /
    ///     <c>value</c> pair, but nothing in the app ever constructed one — only a unit test did — so no
    ///     such line was ever written to any file. The keys removed were never on disk, and the message
    ///     line's key set is untouched, so every transcript ever produced serializes to the same bytes
    ///     before and after. Bumping would have announced a shape change to readers that cannot observe
    ///     one. See issue #264 for why the feature was unreachable in the first place.
    /// </remarks>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Discriminator value for a persisted-message line.</summary>
    public const string MessageLineType = "message";

    /// <summary>Length in characters of a derived <see cref="Uid"/>.</summary>
    public const int UidLength = 8;

    /// <summary>
    ///     Length in characters of a short id used inside a file leaf — 50 bits of hash, base32.
    /// </summary>
    /// <remarks>
    ///     <b>A file leaf's short id is an identity, not a display abbreviation, so it is sized for
    ///     collision resistance rather than for looks.</b> Two conversations whose titles slug the same
    ///     (the common case: "Untitled", "Code review", the empty slug) differ only by this suffix, so a
    ///     collision does not produce two similar names — it produces ONE file that both conversations
    ///     append to, interleaved, each one's watermark rewinding the other's, and no downstream reader can
    ///     separate them again. At 4 characters (20 bits) that is a ~3% chance across 256 same-slug files
    ///     in one workspace and a coin flip by ~1200, which is well inside what a long-lived agent
    ///     workspace accumulates. Ten characters (50 bits) puts the same 256-file workspace at ~3e-11.
    ///     <para>
    ///     Deliberately NOT the same value as <see cref="UidLength"/>. A uid is scoped to one file and one
    ///     conversation's rows; this is scoped to every conversation that ever wrote into a workspace, so
    ///     they carry different collision domains and must be tuned separately.
    ///     </para>
    ///     <para>
    ///     The cost is 6 characters of leaf. With <see cref="MaxSlugLength"/> at 80 the longest leaf is
    ///     80 + 1 + 10 = 91 characters plus <c>.jsonl</c> or <c>_agents</c> — still far inside the 255-byte
    ///     path-component cap.
    ///     </para>
    /// </remarks>
    public const int ShortIdLength = 10;

    /// <summary>Leaf pinned for a sub-agent whose display name is null or slugs to nothing.</summary>
    public const string UnnamedAgentLeafPrefix = "agent";

    /// <summary>
    ///     Longest slug retained in a file leaf. A title is user-authored and unbounded; most filesystems
    ///     cap a path component at 255 bytes, and the leaf still has to fit a short id, a separator and
    ///     <c>.jsonl</c>.
    /// </summary>
    private const int MaxSlugLength = 80;

    /// <summary>RFC 4648 base32 alphabet, lowercased — the encoding <see cref="Uid"/> uses.</summary>
    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>Per-line schema version. See <see cref="CurrentSchemaVersion"/> for the bump rule.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    ///     The discriminator. <see cref="MessageLineType"/> is the only value produced today.
    /// </summary>
    /// <remarks>
    ///     Kept as an open <c>string</c> rather than collapsed into a constant, because it is what lets a
    ///     second line type be added later without every existing reader having to be taught that a line
    ///     it does not recognize is still a line. It is also why five members below
    ///     (<see cref="RunId"/>, <see cref="MessageType"/>, <see cref="Role"/>, <see cref="Id"/>,
    ///     <see cref="MessageJson"/>) are nullable while their <see cref="PersistedMessage"/> sources are
    ///     <c>required</c>: they were widened for a <c>state</c> line that has since been deleted, and are
    ///     left widened deliberately rather than tightened and re-widened. <see cref="ForMessage"/> is the
    ///     only writer and copies each one from a <c>required</c> source, so none of them is ever null in
    ///     practice.
    /// </remarks>
    public required string Type { get; init; }

    /// <summary>
    ///     Stable identity of this line: truncated SHA-256 of the source id, base32 lowercase, exactly
    ///     <see cref="UidLength"/> characters. Deterministic across flushes and across processes.
    /// </summary>
    public required string Uid { get; init; }

    /// <summary>
    ///     <c>uid</c> of the previous line in store order, or null for the first line of a file.
    ///     <b>Honest caveat:</b> this is STORE ADJACENCY. It recovers <i>sequence</i>, not turn
    ///     structure — a reader can replay the order rows were persisted in, but cannot infer from the
    ///     chain alone which rows belonged to the same turn.
    /// </summary>
    public string? ParentUid { get; init; }

    /// <summary>Thread the line belongs to.</summary>
    public required string ThreadId { get; init; }

    /// <summary>ISO-8601 UTC instant, round-trip ("O") format.</summary>
    public required string Timestamp { get; init; }

    /// <summary>
    ///     Sub-agent DISPLAY NAME, from <see cref="SubAgentProvenance.NameKey"/>; null on a main-file
    ///     line. Never <see cref="PersistedMessage.FromAgent"/> — providers put a provider message id in
    ///     that field, so sourcing <c>agent</c> from it would fill the column with opaque ids.
    /// </summary>
    public string? Agent { get; init; }

    /// <summary>Run the message was produced in. Message lines only.</summary>
    public string? RunId { get; init; }

    /// <summary>Parent run, for branched lineage. Message lines only.</summary>
    public string? ParentRunId { get; init; }

    /// <summary>Generation (turn) the message belongs to. Message lines only.</summary>
    public string? GenerationId { get; init; }

    /// <summary>Order of the message within its generation. Message lines only.</summary>
    public int? MessageOrderIdx { get; init; }

    /// <summary>Concrete message type name (e.g. <c>TextMessage</c>). Message lines only.</summary>
    public string? MessageType { get; init; }

    /// <summary>PascalCase role — see the casing note on the type. Message lines only.</summary>
    public string? Role { get; init; }

    /// <summary>The persisted row's own id, the input the <see cref="Uid"/> was derived from.</summary>
    public string? Id { get; init; }

    /// <summary>
    ///     The serialized message, as an OPAQUE STRING. Message lines only. See the type remarks.
    /// </summary>
    public string? MessageJson { get; init; }

    /// <summary>
    ///     Builds a message line from a persisted row. <paramref name="parentUid"/> is the previous
    ///     line's <see cref="Uid"/> in store order (null for the first line of a file);
    ///     <paramref name="agent"/> is the sub-agent display name for an <c>_agents/</c> file, null for
    ///     the main file.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static WorkspaceTranscriptLine ForMessage(
        PersistedMessage message,
        string? parentUid = null,
        string? agent = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        // No fallback branch for a missing id: PersistedMessage.Id is `required`, so the 6-tuple
        // fallback the issue body sketched is unreachable code and is deliberately not written.
        return new WorkspaceTranscriptLine
        {
            Type = MessageLineType,
            Uid = DeriveUid(message.Id),
            ParentUid = parentUid,
            ThreadId = message.ThreadId,
            Timestamp = FormatTimestamp(message.Timestamp),
            Agent = agent,
            RunId = message.RunId,
            ParentRunId = message.ParentRunId,
            GenerationId = message.GenerationId,
            MessageOrderIdx = message.MessageOrderIdx,
            MessageType = message.MessageType,
            Role = NormalizeRole(message.Role),
            Id = message.Id,
            MessageJson = message.MessageJson,
        };
    }

    /// <summary>
    ///     Projects a sequence of persisted rows into chained message lines: each line's
    ///     <see cref="ParentUid"/> is the previous line's <see cref="Uid"/>, and the first line's is
    ///     <paramref name="rootParentUid"/>. For an <c>_agents/</c> file that root pointer is the
    ///     <c>uid</c> of the line in the MAIN file the sub-agent hangs off, which is what makes the chain
    ///     resolvable across the conversation's whole file set rather than only within one file.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public static IReadOnlyList<WorkspaceTranscriptLine> ChainMessages(
        IReadOnlyList<PersistedMessage> messages,
        string? agent = null,
        string? rootParentUid = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var lines = new List<WorkspaceTranscriptLine>(messages.Count);
        var parentUid = rootParentUid;
        foreach (var message in messages)
        {
            var line = ForMessage(message, parentUid, agent);
            lines.Add(line);
            parentUid = line.Uid;
        }

        return lines;
    }

    /// <summary>
    ///     Serializes one line to its exact on-disk JSON — compact, single-line, snake_case, with a
    ///     PINNED key order and a PINNED key SET.
    /// </summary>
    /// <remarks>
    ///     Written by hand rather than via reflection so two guarantees are structural instead of
    ///     incidental: (1) every message line carries the SAME key set regardless of message kind —
    ///     absent values are emitted as JSON <c>null</c>, never omitted, so a columnar reader sees one
    ///     stable schema; (2) <c>message_json</c> is emitted with <c>WriteString</c>, so it cannot ever
    ///     become an object. The default encoder escapes control characters, so an embedded newline is
    ///     written as <c>\n</c> and one record is always exactly one line.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    public static string Serialize(WorkspaceTranscriptLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", line.SchemaVersion);
            writer.WriteString("type", line.Type);
            writer.WriteString("uid", line.Uid);
            WriteNullableString(writer, "parent_uid", line.ParentUid);

            WriteNullableString(writer, "agent", line.Agent);
            writer.WriteString("thread_id", line.ThreadId);
            WriteNullableString(writer, "run_id", line.RunId);
            WriteNullableString(writer, "parent_run_id", line.ParentRunId);
            WriteNullableString(writer, "generation_id", line.GenerationId);
            if (line.MessageOrderIdx is { } idx)
            {
                writer.WriteNumber("message_order_idx", idx);
            }
            else
            {
                writer.WriteNull("message_order_idx");
            }

            writer.WriteString("timestamp", line.Timestamp);
            WriteNullableString(writer, "message_type", line.MessageType);
            WriteNullableString(writer, "role", line.Role);
            WriteNullableString(writer, "id", line.Id);
            WriteNullableString(writer, "message_json", line.MessageJson);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    ///     Derives a line identity from <paramref name="seed"/>: SHA-256, truncated, base32 lowercase,
    ///     exactly <see cref="UidLength"/> characters. Pure and deterministic — the same seed yields the
    ///     same value in every process and on every flush, which is what lets an already-appended line be
    ///     recognized rather than re-appended.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="seed"/> is null.</exception>
    public static string DeriveUid(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return Base32Lower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)), UidLength);
    }

    /// <summary>
    ///     Derives the short id a file leaf carries. Hashed rather than prefix-sliced because ids in this
    ///     sample share prefixes (<c>subagent-</c>) and may contain characters a filename cannot, so a
    ///     prefix would be neither legal nor discriminating.
    /// </summary>
    /// <remarks>
    ///     A truncated hash is collision-RESISTANT, not collision-free; <see cref="ShortIdLength"/> is what
    ///     sets the margin, and its remarks state the numbers. Nothing downstream may treat this value as
    ///     an injective encoding of <paramref name="id"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    public static string ShortId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Base32Lower(SHA256.HashData(Encoding.UTF8.GetBytes(id)), ShortIdLength);
    }

    /// <summary>
    ///     Main transcript file leaf (no extension): <c>{slug(title)}-{shortThreadId}</c>. The short-id
    ///     suffix is what keeps the leaf non-empty when the title slugs to nothing, and what separates two
    ///     conversations whose titles slug identically — to within <see cref="ShortIdLength"/>'s collision
    ///     margin, which is the only thing standing between them and one interleaved file.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="shortThreadId"/> is blank.</exception>
    public static string MainFileLeaf(string? title, string shortThreadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortThreadId);

        var slug = Slug(title);
        return slug.Length == 0 ? shortThreadId : $"{slug}-{shortThreadId}";
    }

    /// <summary>
    ///     Sub-agent transcript file leaf (no extension): <c>{slug(name)}-{shortAgentId}</c>, pinned to
    ///     <c>agent-{shortAgentId}</c> when the name is null or slugs to nothing. Two sub-agents sharing a
    ///     display name still get distinct leaves, because the short id comes from the agent id — again to
    ///     within <see cref="ShortIdLength"/>'s margin.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="shortAgentId"/> is blank.</exception>
    public static string AgentFileLeaf(string? agentName, string shortAgentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortAgentId);

        var slug = Slug(agentName);
        return slug.Length == 0
            ? $"{UnnamedAgentLeafPrefix}-{shortAgentId}"
            : $"{slug}-{shortAgentId}";
    }

    /// <summary>Formats a Unix-milliseconds instant as ISO-8601 UTC, round-trip ("O") format.</summary>
    public static string FormatTimestamp(long unixMillis) =>
        DateTimeOffset
            .FromUnixTimeMilliseconds(unixMillis)
            .UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    ///     Canonicalizes a persisted role string to the PascalCase name of a <see cref="MessageRole"/>
    ///     member. Parsing is case-insensitive so a row stored in either casing lands on the same value,
    ///     and an unrecognized or absent role reports <see cref="MessageRole.None"/> rather than being
    ///     dropped — <c>None</c> is a real member, not an error code, and it must classify like the other
    ///     four.
    /// </summary>
    public static string NormalizeRole(string? role) =>
        Enum.TryParse<MessageRole>(role, ignoreCase: true, out var parsed)
            ? parsed.ToString()
            : nameof(MessageRole.None);

    /// <summary>
    ///     ASCII-only slugifier: lowercases, keeps <c>[a-z0-9]</c>, collapses every other run to a single
    ///     hyphen, never emits a leading or trailing hyphen, and truncates to
    ///     <see cref="MaxSlugLength"/>. A title containing <c>/</c>, <c>\</c>, <c>..</c> or a leading
    ///     <c>/</c> therefore produces a flat, legal leaf and never throws.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>This is a deliberate SEVENTH copy of a slug helper in this repo — none of the six existing
    ///     ones is usable here.</b> All six branch on <c>char.IsLetterOrDigit</c>, which is
    ///     Unicode-aware: it answers true for CJK ideographs, Cyrillic, accented Latin and more, so they
    ///     pass those characters straight through into a filename. This one must not — a transcript leaf
    ///     is written into a Linux sandbox by a shell splice and read back by tooling that assumes ASCII
    ///     paths. Three of the six also live in <c>CodeReviewDaemon.Sample</c> (a different assembly) and
    ///     the rest are <c>private</c>, so none was reachable either. Consolidating the seven is a
    ///     recorded, locked deferral.
    ///     </para>
    ///     <para>
    ///     Existing copies, for whoever does that consolidation:
    ///     <c>AnthropicCompatProviders.Slugify</c>, <c>WorkflowRunRegistry</c>'s inline thread-id
    ///     sanitizer, <c>ReviewBranchManager.Slug</c>, <c>KnowledgeAgent.Slugify</c>,
    ///     <c>KnowledgeAgent.SlugFromRelPath</c>, <c>DaemonReviewStageExecutor.SlugSegment</c>.
    ///     </para>
    /// </remarks>
    internal static string Slug(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxSlugLength));
        var pendingSeparator = false;

        foreach (var ch in value)
        {
            var lower = ch is >= 'A' and <= 'Z' ? (char)(ch + ('a' - 'A')) : ch;
            if (lower is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                // Everything else — punctuation, path separators, whitespace, and every non-ASCII
                // character — becomes a pending separator that is only emitted if another kept
                // character follows. That is what keeps leading/trailing hyphens out.
                pendingSeparator = true;
                continue;
            }

            if (pendingSeparator && builder.Length > 0)
            {
                if (builder.Length + 1 >= MaxSlugLength)
                {
                    break;
                }

                _ = builder.Append('-');
            }

            _ = builder.Append(lower);
            pendingSeparator = false;

            if (builder.Length >= MaxSlugLength)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>
    ///     Encodes the leading bits of <paramref name="hash"/> as RFC 4648 base32, lowercased, MSB-first,
    ///     unpadded — <paramref name="length"/> characters carry <c>length * 5</c> bits.
    /// </summary>
    private static string Base32Lower(ReadOnlySpan<byte> hash, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            var bitOffset = i * 5;
            var byteIndex = bitOffset >> 3;
            var bitIndex = bitOffset & 7;

            // Read a 16-bit window starting at byteIndex, then slide the wanted 5 bits down to the
            // bottom. SHA-256 is 32 bytes, so the window never runs past the end for any length here.
            var window = (hash[byteIndex] << 8) | hash[byteIndex + 1];
            chars[i] = Base32Alphabet[(window >> (11 - bitIndex)) & 0x1F];
        }

        return new string(chars);
    }
}
