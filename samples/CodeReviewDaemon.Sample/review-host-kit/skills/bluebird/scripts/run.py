#!/usr/bin/env python3
"""Portable Agency skill runner. Python 3.10+ standard library only."""

import argparse
import inspect
import json
import os
import sys
from pathlib import Path

from generated import Client
from generated._runtime import BridgeError, load_policy, prepare, approval_digest, validate_url


POLICY = load_policy(Client.SERVICE)
ALLOWED = tuple(POLICY["tools"])


def description(name):
    method = getattr(Client, name)
    params = {}
    for key, param in inspect.signature(method).parameters.items():
        if key in ("self", "extra"):
            continue
        params[key] = {"type": str(param.annotation).replace("<class '", "").replace("'>", ""),
                       "required": param.default is inspect.Parameter.empty}
        if param.default is not inspect.Parameter.empty:
            params[key]["default"] = param.default
    return {"name": name, "parameters": params, "description": inspect.getdoc(method), **POLICY["tools"][name]}


def reject_constant(value):
    raise ValueError("Non-finite JSON numbers are not supported.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout", type=float, default=None, help="Request timeout in seconds, 1–3600; defaults to service timeout")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("catalog", help="Offline tool names and approval requirements")
    describe = commands.add_parser("describe", help="Offline arguments, policy and documentation")
    describe.add_argument("tool", choices=ALLOWED)
    schema = commands.add_parser("schema", help="Live schemas and bundled tool-name drift")
    schema.add_argument("tool", choices=ALLOWED, nargs="?")
    for command in ("preview", "call"):
        sub = commands.add_parser(command, help="Validate locally" if command == "preview" else "Execute one explicitly selected tool")
        sub.add_argument("tool", choices=ALLOWED)
        sub.add_argument("--input", default="-", help="UTF-8 JSON file path or - for stdin")
        if command == "call":
            sub.add_argument("--approve", help="Request digest from preview, only after human approval of the exact action")
    options = parser.parse_args()
    try:
        if options.command == "catalog":
            result = {"service": Client.SERVICE, "tools": [
                {"name": name, "wire_name": POLICY["tools"][name]["wire_name"],
                 "approval": POLICY["tools"][name]["approval"], "reason": POLICY["tools"][name]["reason"],
                 "description": (inspect.getdoc(getattr(Client, name)) or "").split("\n\n")[0]}
                for name in ALLOWED]}
        elif options.command == "describe":
            result = description(options.tool)
        elif options.command == "schema":
            result = Client(timeout=options.timeout).schema(options.tool)
        else:
            try:
                raw = sys.stdin.read() if options.input == "-" else Path(options.input).read_text(encoding="utf-8")
                arguments = json.loads(raw, parse_constant=reject_constant)
            except (OSError, UnicodeError, ValueError):
                raise ValueError("Cannot read input: provide a UTF-8 JSON object via stdin or --input.") from None
            arguments, wire = prepare(Client, POLICY, options.tool, arguments)
            entry = POLICY["tools"][options.tool]
            if options.command == "preview":
                origin = validate_url(os.environ.get("MCP_BRIDGE_URL", "http://127.0.0.1:8765"))
                result = {"service": Client.SERVICE, "tool": options.tool, "wire_name": entry["wire_name"],
                          "bridge": origin, "arguments": arguments, "wire_arguments": wire,
                          "approval_required": entry["approval"], "reason": entry["reason"],
                          "approval_digest": approval_digest(origin, Client.SERVICE, entry["wire_name"], wire),
                          "notice": "Local preview only. Digest is not proof of consent; obtain human approval before using it. No network request made."}
            else:
                client = Client(timeout=options.timeout, approval=options.approve)
                result = client.invoke(options.tool, arguments)
                print(json.dumps(result, ensure_ascii=False, allow_nan=False))
                client.call_checked(result)
                return 0
        # Preview may contain user data; do not persist it or paste it into public logs.
        output = json.dumps(result, ensure_ascii=False, allow_nan=False)
        token = os.environ.get("MCP_BRIDGE_TOKEN")
        if token:
            output = output.replace(token, "[REDACTED]")
        print(output)
        return 0
    except (BridgeError, ValueError, TypeError) as exc:
        print(str(exc), file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("Interrupted; no retry attempted. An action may still be running remotely.", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
