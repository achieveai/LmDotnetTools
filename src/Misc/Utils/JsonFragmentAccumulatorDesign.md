# 1. Purpose & Goals

We want a single, reusable component that:

• Accepts arbitrary JSON text *fragments* (from a streaming source).  
• Maintains parse state across calls (so partial tokens, nested objects/arrays, etc. are handled).  
• Emits incremental “path + value” events as soon as they become available:  
  – Partial string content (as you read more of a string)  
  – Complete scalars (numbers, booleans, null)  
  – Array entries (partial and complete)  
  – Object starts/ends, array starts/ends  

This replaces brittle regex or dual‑buffer hacks with a true visitor over a dynamic parse tree.

---

# 2. Requirements

1. **Streaming**: Fragments may split tokens anywhere (e.g. mid‑string, mid‑number).  
2. **Resumable**: You must pause on fragment end and resume parsing when more text arrives.  
3. **Path Tracking**: Every emitted value must carry its JSON path (e.g. `root.items[3].name`).  
4. **Partial & Complete**:  
   - While inside a string, emit each new substring chunk.  
   - Once a number/boolean/null finishes, emit it once.  
   - On object/array open and close, emit structural events.

---

# 3. Public API

```csharp
class JsonFragmentAccumulator
{
  // Feed the next text fragment. Returns zero or more updates.
  IEnumerable<JsonFragmentUpdate> AddFragment(string fragment);

  // True if the overall JSON text seen so far appears to be a complete, balanced document.
  bool IsComplete { get; }

  // Reset internal state to start over.
  void Reset();
}
```

Each `JsonFragmentUpdate` holds:
- `string Path` (e.g. “users[1].address.street”)
- `JsonFragmentKind Kind` (StartObject, EndArray, PartialString, CompleteNumber, etc.)
- `string? TextValue` or `JsonValue?` (the fragment or complete value)

---

# 4. Internal Architecture

1. **Character‐by‐character state machine**:  
   - Processes incoming chars one by one.  
   - Maintains a stack of container contexts (object vs. array).

2. **Stack of Context Frames**  
   Each frame represents an open object or array:  
   ```text
   class Frame {
     enum ContainerType { Object, Array }
     ContainerType Type;
     // For objects: the current property name (partial or complete)
     StringBuilder CurrentPropertyName;
     // For arrays: next index integer
     int NextArrayIndex;
     // When inside a value (string/number/bool/null), track a ValueBuffer
     ValueBuffer CurrentValue;
   }
   ```
   - The root has a special “virtual object” frame.

3. **ValueBuffer**  
   A small helper that tracks:
   - `enum ValueKind { None, String, Number, Boolean, Null }`
   - A `StringBuilder Buffer` accumulating text for that token.
   - For strings, tracks whether the last char was a backslash (escape).

4. **Position Tracking**  
   - Absolute offset in the stream if needed.  
   - The current JSON path is derived by walking the stack:  
     e.g. for each object frame, use its `CurrentPropertyName`; for each array frame, use `[index]`.

---

# 5. Parse & Emit Logic

1. **On AddFragment**  
   - Append fragment to internal char queue.  
   - While there are unprocessed chars, call `ProcessChar(ch)`.

2. **ProcessChar(ch)**  
   a. If inside a string (`CurrentValue.Kind==String`):  
      - Append char (handling escapes).  
      - If closing quote and not escaped ⇒ end‑of‑string: emit `CompleteString` and clear buffer.  
      - Else emit `PartialString` with the latest chunk.  
   b. Else if reading a number/bool/null (`CurrentValue.Kind==...`):  
      - If char valid for token ⇒ append; continue.  
      - Else token ended: emit `CompleteNumber` (or boolean/null), clear buffer, re–process this char as structural.  
   c. Else (not in middle of token):  
      - If char is `{` or `[` ⇒ push new Frame, emit `StartObject`/`StartArray`.  
      - If char is `}` or `]` ⇒ pop Frame, emit `EndObject`/`EndArray`, and update parent frame (e.g. increment array index).  
      - If char is `"` ⇒ start new string token: set `CurrentValue.Kind=String`, emit `StartString`.  
      - If digit, `-` ⇒ start number token.  
      - If alpha starts `true|false|null` ⇒ start Boolean/Null token.  
      - If whitespace or comma or colon ⇒ skip or advance property vs. value mode.

3. **Property Names in Objects**  
   - After `StartObject`, expect a string token for the property name:  
     – Buffer the property name similarly to a value string.  
     – On `CompleteString` inside an object and before a colon, assign it to the frame’s `CurrentPropertyName`.

4. **Paths**  
   Each emit uses the stack to compute path:  
   - For each object frame except root, join keys with `.`.  
   - For array frames, use `[index]`.  
   - E.g. `root.users[2].email`.

5. **Completeness Check**  
   - Balanced braces/brackets (stack count == 1 only root remains).  
   - No in‑flight token (not mid‑string or mid‑number).  
   - That yields `IsComplete=true`.

---

# 6. Event Types (`JsonFragmentKind`)

- `StartObject`, `EndObject`  
- `StartArray`, `EndArray`  
- `StartString`, `PartialString`, `CompleteString`  
- `CompleteNumber`, `CompleteBoolean`, `CompleteNull`

Clients consume the stream of `JsonFragmentUpdate`s in order.

---

# 7. Error & Recovery

- If you see invalid JSON syntax, you may choose to:  
  1. Skip to next structural token and resume.  
  2. Surface a parse error event and clear buffer.  

(Exact policy can be tuned.)

---

# 8. Testing Strategy

1. **Simple**: Single fragment covering small JSON (flat object, array).  
2. **Split tokens**: Feed a JSON string literal broken between fragments, verify partial/complete emits.  
3. **Nested**: Multi‑level objects/arrays.  
4. **Scalars**: Numbers split into “123” versus “45” across fragments.  
5. **Edge cases**: Escaped quotes, Unicode escapes, empty strings, empty arrays/objects, trailing commas.  
6. **Completeness**: Confirm `IsComplete` only true when fully balanced.

---

# 9. Integration

Replace all existing ad‑hoc buffers in `ConsolePrinterHelper` and `CustomToolFormatterFactory` with a single `JsonFragmentAccumulator` instance (per logical JSON stream). On each text update, call `AddFragment(...)` and render the returned updates immediately.

---

This design gives a robust, resumable, stack‑based JSON visitor that naturally supports the four categories of incremental notifications you need.