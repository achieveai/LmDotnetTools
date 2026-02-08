#!/usr/bin/env bash
# Anthropic/Kimi Server Tool SSE Capture Scripts
#
# Usage:
#   # For Kimi:
#   export ANTHROPIC_API_KEY="your-kimi-key"
#   export ANTHROPIC_BASE_URL="https://api.kimi.com/coding"
#   export ANTHROPIC_MODEL="kimi-2.5"
#
#   # For Anthropic:
#   export ANTHROPIC_API_KEY="your-anthropic-key"
#   export ANTHROPIC_BASE_URL="https://api.anthropic.com/v1"
#   export ANTHROPIC_MODEL="claude-sonnet-4-20250514"
#
#   # Run individual request:
#   bash curl-anthropic-requests.sh 1
#
#   # Save SSE output to file for replay testing:
#   bash curl-anthropic-requests.sh 1 > captures/turn1-kimi.txt 2>/dev/null

set -euo pipefail

BASE_URL="${ANTHROPIC_BASE_URL:-https://api.anthropic.com/v1}"
API_KEY="${ANTHROPIC_API_KEY:?Set ANTHROPIC_API_KEY}"
MODEL="${ANTHROPIC_MODEL:-claude-sonnet-4-20250514}"

# ─── 1. Turn 1 - single web_search ───────────────────────────────────────────
request_1() {
curl -N "${BASE_URL}/messages" \
  -H "x-api-key: ${API_KEY}" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{
  "model": "'"${MODEL}"'",
  "max_tokens": 4096,
  "stream": true,
  "system": "You are a research assistant with access to web search. Use your web search capability to find accurate answers. Be concise - limit response to 2-3 sentences.",
  "tools": [
    { "type": "web_search_20250305", "name": "web_search" }
  ],
  "messages": [
    {
      "role": "user",
      "content": [{ "type": "text", "text": "What are the top AI companies in 2026?" }]
    }
  ]
}'
}

# ─── 2. Turn 2 - multi-turn with server_tool_use history ─────────────────────
# Replace TURN1_TOOL_ID with the server_tool_use.id from request 1's response
request_2() {
local TURN1_TOOL_ID="${1:-srvtoolu_REPLACE_ME}"
curl -N "${BASE_URL}/messages" \
  -H "x-api-key: ${API_KEY}" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{
  "model": "'"${MODEL}"'",
  "max_tokens": 4096,
  "stream": true,
  "system": "You are a research assistant with access to web search. Use your web search capability to find accurate answers. Be concise - limit response to 2-3 sentences.",
  "tools": [
    { "type": "web_search_20250305", "name": "web_search" }
  ],
  "messages": [
    {
      "role": "user",
      "content": [{ "type": "text", "text": "What are the top AI companies in 2026?" }]
    },
    {
      "role": "assistant",
      "content": [
        { "type": "text", "text": "Let me search for that." },
        {
          "type": "server_tool_use",
          "id": "'"${TURN1_TOOL_ID}"'",
          "name": "web_search",
          "input": { "query": "top AI companies 2026" }
        }
      ]
    },
    {
      "role": "user",
      "content": [
        {
          "type": "tool_result",
          "tool_use_id": "'"${TURN1_TOOL_ID}"'",
          "content": "{}"
        }
      ]
    },
    {
      "role": "assistant",
      "content": [
        { "type": "text", "text": "The top AI companies include Anthropic, OpenAI, Google DeepMind, and Meta AI." }
      ]
    },
    {
      "role": "user",
      "content": [{ "type": "text", "text": "Tell me more about Anthropic specifically" }]
    }
  ]
}'
}

# ─── 3. Turn 2 - mismatched IDs (to test what Kimi/Anthropic returns) ────────
request_3() {
curl -N "${BASE_URL}/messages" \
  -H "x-api-key: ${API_KEY}" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{
  "model": "'"${MODEL}"'",
  "max_tokens": 4096,
  "stream": true,
  "system": "You are a research assistant.",
  "tools": [
    { "type": "web_search_20250305", "name": "web_search" }
  ],
  "messages": [
    {
      "role": "user",
      "content": [{ "type": "text", "text": "Search for something" }]
    },
    {
      "role": "assistant",
      "content": [
        {
          "type": "server_tool_use",
          "id": "srvtoolu_valid_id_aaa",
          "name": "web_search",
          "input": { "query": "test" }
        }
      ]
    },
    {
      "role": "user",
      "content": [
        {
          "type": "tool_result",
          "tool_use_id": "srvtoolu_MISMATCHED_bbb",
          "content": "{}"
        }
      ]
    }
  ]
}'
}

# ─── 4. Turn 1 - non-streaming (complete JSON response) ──────────────────────
request_4() {
curl "${BASE_URL}/messages" \
  -H "x-api-key: ${API_KEY}" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{
  "model": "'"${MODEL}"'",
  "max_tokens": 4096,
  "stream": false,
  "system": "You are a research assistant with access to web search. Be concise - one sentence.",
  "tools": [
    { "type": "web_search_20250305", "name": "web_search" }
  ],
  "messages": [
    {
      "role": "user",
      "content": [{ "type": "text", "text": "What is the current price of Bitcoin?" }]
    }
  ]
}'
}

# ─── 5. Turn 3 - full 3-turn history ─────────────────────────────────────────
request_5() {
local T1_ID="${1:-srvtoolu_turn1_id}"
local T2_ID="${2:-srvtoolu_turn2_id}"
curl -N "${BASE_URL}/messages" \
  -H "x-api-key: ${API_KEY}" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{
  "model": "'"${MODEL}"'",
  "max_tokens": 4096,
  "stream": true,
  "system": "You are a research assistant with access to web search. Be concise.",
  "tools": [
    { "type": "web_search_20250305", "name": "web_search" }
  ],
  "messages": [
    {
      "role": "user",
      "content": [{ "type": "text", "text": "What are the top AI companies?" }]
    },
    {
      "role": "assistant",
      "content": [
        {
          "type": "server_tool_use",
          "id": "'"${T1_ID}"'",
          "name": "web_search",
          "input": { "query": "top AI companies 2026" }
        }
      ]
    },
    {
      "role": "user",
      "content": [
        { "type": "tool_result", "tool_use_id": "'"${T1_ID}"'", "content": "{}" }
      ]
    },
    {
      "role": "assistant",
      "content": [
        { "type": "text", "text": "The top AI companies include Anthropic, OpenAI, Google DeepMind, and Meta AI." }
      ]
    },
    {
      "role": "user",
      "content": [{ "type": "text", "text": "Tell me about Anthropic" }]
    },
    {
      "role": "assistant",
      "content": [
        {
          "type": "server_tool_use",
          "id": "'"${T2_ID}"'",
          "name": "web_search",
          "input": { "query": "Anthropic AI company 2026" }
        }
      ]
    },
    {
      "role": "user",
      "content": [
        { "type": "tool_result", "tool_use_id": "'"${T2_ID}"'", "content": "{}" }
      ]
    },
    {
      "role": "assistant",
      "content": [
        { "type": "text", "text": "Anthropic is an AI safety company founded by Dario and Daniela Amodei." }
      ]
    },
    {
      "role": "user",
      "content": [{ "type": "text", "text": "How does Claude compare to GPT?" }]
    }
  ]
}'
}

# ─── 6. web_search + function tools combined ──────────────────────────────────
request_6() {
curl -N "${BASE_URL}/messages" \
  -H "x-api-key: ${API_KEY}" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{
  "model": "'"${MODEL}"'",
  "max_tokens": 4096,
  "stream": true,
  "system": "You are a research assistant. Use web search for research and get_weather for weather.",
  "tools": [
    { "type": "web_search_20250305", "name": "web_search" },
    {
      "name": "get_weather",
      "description": "Get current weather conditions for a specific location",
      "input_schema": {
        "type": "object",
        "properties": {
          "location": { "type": "string", "description": "City name" }
        },
        "required": ["location"]
      }
    }
  ],
  "messages": [
    {
      "role": "user",
      "content": [{ "type": "text", "text": "What is the latest news about AI and what is the weather in San Francisco?" }]
    }
  ]
}'
}

# ─── 7. No input field on server_tool_use in history ──────────────────────────
request_7() {
curl -N "${BASE_URL}/messages" \
  -H "x-api-key: ${API_KEY}" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{
  "model": "'"${MODEL}"'",
  "max_tokens": 4096,
  "stream": true,
  "system": "You are a research assistant.",
  "tools": [
    { "type": "web_search_20250305", "name": "web_search" }
  ],
  "messages": [
    {
      "role": "user",
      "content": [{ "type": "text", "text": "Search for the meaning of life" }]
    },
    {
      "role": "assistant",
      "content": [
        {
          "type": "server_tool_use",
          "id": "srvtoolu_no_input",
          "name": "web_search"
        }
      ]
    },
    {
      "role": "user",
      "content": [
        { "type": "tool_result", "tool_use_id": "srvtoolu_no_input", "content": "{}" }
      ]
    },
    {
      "role": "assistant",
      "content": [
        { "type": "text", "text": "The meaning of life is 42." }
      ]
    },
    {
      "role": "user",
      "content": [{ "type": "text", "text": "Can you elaborate?" }]
    }
  ]
}'
}

# ─── 8. web_search_tool_result type in history (old format) ───────────────────
# Tests whether the provider accepts web_search_tool_result in user message
# vs tool_result (our new format)
request_8() {
curl -N "${BASE_URL}/messages" \
  -H "x-api-key: ${API_KEY}" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{
  "model": "'"${MODEL}"'",
  "max_tokens": 4096,
  "stream": true,
  "system": "You are a research assistant.",
  "tools": [
    { "type": "web_search_20250305", "name": "web_search" }
  ],
  "messages": [
    {
      "role": "user",
      "content": [{ "type": "text", "text": "What is quantum computing?" }]
    },
    {
      "role": "assistant",
      "content": [
        {
          "type": "server_tool_use",
          "id": "srvtoolu_abc123",
          "name": "web_search",
          "input": { "query": "quantum computing" }
        }
      ]
    },
    {
      "role": "user",
      "content": [
        {
          "type": "web_search_tool_result",
          "tool_use_id": "srvtoolu_abc123",
          "content": [
            { "type": "web_search_result", "url": "https://example.com", "title": "Quantum", "encrypted_content": "dGVzdA==", "page_age": "1d" }
          ]
        }
      ]
    },
    {
      "role": "assistant",
      "content": [
        { "type": "text", "text": "Quantum computing uses qubits." }
      ]
    },
    {
      "role": "user",
      "content": [{ "type": "text", "text": "Tell me more" }]
    }
  ]
}'
}

# ─── Runner ───────────────────────────────────────────────────────────────────
case "${1:-help}" in
  1) echo "# Request 1: Turn 1 - single web_search" >&2; request_1 ;;
  2) echo "# Request 2: Turn 2 - multi-turn (pass TURN1_TOOL_ID as \$2)" >&2; request_2 "${2:-}" ;;
  3) echo "# Request 3: Mismatched IDs error test" >&2; request_3 ;;
  4) echo "# Request 4: Non-streaming" >&2; request_4 ;;
  5) echo "# Request 5: Full 3-turn (pass T1_ID as \$2, T2_ID as \$3)" >&2; request_5 "${2:-}" "${3:-}" ;;
  6) echo "# Request 6: web_search + function tools" >&2; request_6 ;;
  7) echo "# Request 7: No input on server_tool_use" >&2; request_7 ;;
  8) echo "# Request 8: web_search_tool_result type in history" >&2; request_8 ;;
  *)
    echo "Usage: $0 <request_number> [args...]"
    echo ""
    echo "  export ANTHROPIC_API_KEY=your-key"
    echo "  export ANTHROPIC_BASE_URL=https://api.kimi.com/coding   # or https://api.anthropic.com/v1"
    echo "  export ANTHROPIC_MODEL=kimi-2.5                         # or claude-sonnet-4-20250514"
    echo ""
    echo "Requests:"
    echo "  1           Turn 1 - single web_search (capture IDs from response)"
    echo "  2 [TOOL_ID] Turn 2 - multi-turn with server_tool_use history"
    echo "  3           Mismatched IDs - error test"
    echo "  4           Non-streaming response (JSON, not SSE)"
    echo "  5 [T1] [T2] Turn 3 - full 3-turn history"
    echo "  6           web_search + function tools combined"
    echo "  7           No input field on server_tool_use in history"
    echo "  8           web_search_tool_result type in history (old format vs tool_result)"
    echo ""
    echo "Capture to file:"
    echo "  $0 1 > captures/turn1-kimi.txt 2>/dev/null"
    echo ""
    echo "Extract server_tool_use ID from Turn 1 output:"
    echo "  grep -o '\"id\":\"[^\"]*\"' captures/turn1-kimi.txt | head -1"
    ;;
esac
