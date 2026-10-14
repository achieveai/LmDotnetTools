"""Dependency-free adaptation of test/generated/_runtime.py for portable skills."""

import hashlib
import hmac
import inspect
from pathlib import Path
from typing import Any, get_args
import ipaddress
import json
import math
import os
import socket
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


class BridgeError(RuntimeError):
    """A safe-to-display configuration, HTTP, or protocol error."""


class ToolError(BridgeError):
    """The backend returned an in-band tool failure."""


class NoRedirects(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def validate_url(value):
    try:
        parsed = urlsplit(value)
        port = parsed.port
        host = parsed.hostname
        loopback = host == "localhost"
        if host and not loopback:
            try:
                loopback = ipaddress.ip_address(host).is_loopback
            except ValueError:
                pass
        if (
            not host
            or parsed.username is not None
            or parsed.password is not None
            or parsed.query
            or parsed.fragment
            or parsed.path not in ("", "/")
            or any(c.isspace() or ord(c) < 32 for c in value)
            or (parsed.scheme != "https" and not (parsed.scheme == "http" and loopback))
            or port == 0
        ):
            raise ValueError
    except ValueError:
        raise BridgeError("MCP_BRIDGE_URL must be an HTTPS origin or an HTTP loopback origin; no credentials, path, query, or fragment.") from None
    return value.rstrip("/")


def load_policy(service):
    try:
        policy = json.loads((Path(__file__).resolve().parents[1] / "policy.json").read_text(encoding="utf-8"))
        if policy["service"] != service or not policy["tools"]:
            raise ValueError
        return policy
    except (OSError, ValueError, KeyError):
        raise BridgeError("Missing or invalid bundled policy; rebuild this skill.") from None


def approval_digest(origin, service, tool, arguments):
    data = json.dumps([origin, service, tool, arguments], sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
    return hashlib.sha256(data.encode("utf-8")).hexdigest()


def matches_type(value, annotation):
    if annotation is Any:
        return True
    types = get_args(annotation)
    if types:
        return any(matches_type(value, t) for t in types)
    if annotation is float:
        return type(value) in (int, float)
    return type(value) is annotation


def prepare(cls, policy, name, arguments):
    if name not in policy["tools"] or not isinstance(arguments, dict):
        raise ValueError("Select a bundled tool and provide a JSON object.")
    entry = policy["tools"][name]
    params = {k: v for k, v in inspect.signature(getattr(cls, name)).parameters.items() if k not in ("self", "extra")}
    if arguments.keys() - params.keys():
        raise ValueError("Unknown argument; use describe for Python names (for example from_, not from).")
    args = dict(arguments)
    for key, param in params.items():
        if key not in args:
            if param.default is inspect.Parameter.empty:
                raise ValueError(f"Missing required argument: {key}.")
            continue
        if not matches_type(args[key], param.annotation):
            raise ValueError(f"Invalid type for {key}.")
        if args[key] is None and param.default is not inspect.Parameter.empty:
            del args[key]
    for key, value in entry.get("defaults", {}).items():
        if not (key in ("top", "maxReplies") and "nextLink" in args):
            args.setdefault(key, value)
    for key, values in entry.get("enums", {}).items():
        if key in args and args[key] not in values:
            raise ValueError(f"Unsupported value for {key}; see describe.")
    for key, bounds in entry.get("bounds", {}).items():
        if key in args and (type(args[key]) not in (int, float) or not bounds[0] <= args[key] <= bounds[1]):
            raise ValueError(f"{key} must be between {bounds[0]} and {bounds[1]}.")
    for key in entry.get("nonblank", []):
        if key in args and (not isinstance(args[key], str) or not args[key].strip()):
            raise ValueError(f"Argument {key} must not be blank.")
    for key, needs in entry.get("requires", {}).items():
        if key in args and any(n not in args for n in needs):
            raise ValueError(f"Incomplete scope for {key}.")
    for rule in entry.get("conditional", []):
        if all(args.get(k) == v for k, v in rule["when"].items()):
            if any(k not in args for k in rule.get("required", [])):
                raise ValueError("Missing arguments for the selected operation; see describe.")
            if rule.get("any") and not any(k in args for k in rule["any"]):
                raise ValueError("Provide one of the documented target arguments.")
    for group in entry.get("any_required", []):
        if not any(k in args for k in group):
            raise ValueError("Provide one of the documented target arguments.")
    for key in entry.get("string_lists", []):
        if key in args and any(not isinstance(v, str) or not v.strip() for v in args[key]):
            raise ValueError(f"{key} must contain non-empty strings.")
    for lower, upper in entry.get("ordered", []):
        if lower in args and upper in args and args[upper] < args[lower]:
            raise ValueError(f"{upper} must not precede {lower}.")
    wire = {entry["parameters"][key]: value for key, value in args.items()}
    json.dumps(wire, allow_nan=False)
    return args, wire


class BridgeClient:
    SERVICE = ""
    DEFAULT_TIMEOUT = 180

    def __init__(self, base_url=None, token=None, timeout=None, allowed_tools=None, approval=None):
        self.base_url = validate_url(base_url or os.environ.get("MCP_BRIDGE_URL", "http://127.0.0.1:8765"))
        self._token = token or os.environ.get("MCP_BRIDGE_TOKEN")
        if not self._token:
            raise BridgeError("MCP_BRIDGE_TOKEN is missing. Supply the token printed by the running bridge through the environment.")
        if any(ord(c) < 33 or ord(c) > 126 for c in self._token):
            raise BridgeError("MCP_BRIDGE_TOKEN contains invalid characters.")
        self.timeout = self.DEFAULT_TIMEOUT if timeout is None else timeout
        if not math.isfinite(self.timeout) or not 1 <= self.timeout <= 3600:
            raise BridgeError("Timeout must be between 1 and 3600 seconds.")
        self.policy = load_policy(self.SERVICE)
        self.allowed_tools = frozenset(self.policy["tools"] if allowed_tools is None else allowed_tools)
        self.approval = approval
        self._opener = build_opener(NoRedirects())

    def _request(self, method, path, arguments=None):
        body = None if arguments is None else json.dumps(arguments, allow_nan=False).encode("utf-8")
        request = Request(
            self.base_url + path,
            data=body,
            method=method,
            headers={"Authorization": "Bearer " + self._token, "Content-Type": "application/json"},
        )
        try:
            with self._opener.open(request, timeout=self.timeout) as response:
                raw = response.read()
        except HTTPError as exc:
            status = exc.code
            exc.close()
            guidance = {
                401: "Bridge token missing or expired; refresh it after a bridge restart.",
                403: "Bridge token rejected; refresh it after a bridge restart.",
                404: "Service or tool not found; compare the live schema with the bundled reference.",
                400: "Backend rejected the request; check the live schema and scope.",
                502: "Backend authentication unavailable; the bridge operator must check Windows sign-in/consent.",
                503: "Backend unavailable or timed out; check the bridge runtime.",
            }.get(status, "Request failed; inspect the bridge without exposing credentials.")
            if 300 <= status < 400:
                guidance = "Redirect refused; credentials were not forwarded to the redirect target."
            raise BridgeError(f"HTTP {status}: {guidance}") from None
        except (TimeoutError, socket.timeout):
            raise BridgeError("Bridge request timed out; no automatic retry was attempted.") from None
        except (URLError, OSError, ValueError):
            raise BridgeError("Cannot reach the bridge. Check its URL, TLS trust, and whether it is running.") from None
        try:
            result = json.loads(raw)
        except (ValueError, UnicodeError):
            raise BridgeError("Bridge returned invalid JSON.") from None
        if not isinstance(result, dict):
            raise BridgeError("Bridge returned a non-object result.")
        if "isError" in result and not isinstance(result["isError"], bool):
            raise BridgeError("Bridge returned an invalid isError field.")
        if "content" in result and (
            not isinstance(result["content"], list)
            or any(not isinstance(block, dict) for block in result["content"])
        ):
            raise BridgeError("Bridge returned invalid content blocks.")
        # Never echo the bearer even if a backend includes it in a diagnostic/result.
        return json.loads(json.dumps(result, ensure_ascii=False).replace(self._token, "[REDACTED]"))

    def invoke(self, name, arguments):
        # Use the recorded generated wire mapping without executing method bodies:
        # an upstream method with a parameter named 'arguments' shadows its payload.
        _, wire = prepare(type(self), self.policy, name, arguments)
        return self._call(self.policy["tools"][name]["wire_name"], wire)

    def _call(self, tool_name, arguments):
        matches = [(name, entry) for name, entry in self.policy["tools"].items() if entry["wire_name"] == tool_name]
        if len(matches) != 1 or matches[0][0] not in self.allowed_tools:
            raise BridgeError("Tool is not enabled by this skill.")
        name, entry = matches[0]
        reverse = {wire: key for key, wire in entry["parameters"].items()}
        if arguments.keys() - reverse.keys():
            raise BridgeError("Unknown wire argument; use the bundled method signature.")
        _, arguments = prepare(type(self), self.policy, name, {reverse[k]: v for k, v in arguments.items()})
        if entry.get("approval", True):
            digest = approval_digest(self.base_url, self.SERVICE, tool_name, arguments)
            if not self.approval or not hmac.compare_digest(self.approval, digest):
                raise BridgeError("Approval required: preview this exact request, obtain human consent, then call with --approve DIGEST. No request sent.")
        return self._request("POST", f"/services/{self.SERVICE}/tools/{quote(tool_name, safe='')}", arguments)

    def schema(self, tool_name=None):
        if tool_name is not None:
            if tool_name not in self.allowed_tools or tool_name not in self.policy["tools"]:
                raise BridgeError("Tool is not enabled by this skill.")
            wire_name = self.policy["tools"][tool_name]["wire_name"]
            result = self._request("GET", f"/services/{self.SERVICE}/tools/{quote(wire_name, safe='')}")
            if result.get("name") != wire_name or not isinstance(result.get("inputSchema"), dict):
                raise BridgeError("Bridge returned an invalid tool schema.")
            return result
        result = self._request("GET", f"/services/{self.SERVICE}/tools")
        tools = result.get("tools")
        if not isinstance(tools, list) or any(not isinstance(t, dict) or not isinstance(t.get("name"), str) or not isinstance(t.get("inputSchema"), dict) for t in tools):
            raise BridgeError("Bridge returned an invalid tool catalog.")
        expected = {self.policy["tools"][name]["wire_name"] for name in self.allowed_tools if name in self.policy["tools"]}
        actual = {t["name"] for t in tools}
        return {**result, "tools": [t for t in tools if t["name"] in expected],
                "drift": {"missing": sorted(expected - actual), "unbundled": sorted(actual - expected)}}

    @staticmethod
    def text(result):
        return "\n".join(block.get("text", "") for block in result.get("content", []) if isinstance(block.get("text", ""), str))

    @classmethod
    def call_checked(cls, result):
        if result.get("isError"):
            raise ToolError("Tool reported isError=true; inspect the returned result, not just the HTTP status.")
        return result

    @classmethod
    def help(cls, tool=None):
        for name, fn in inspect.getmembers(cls, predicate=inspect.isfunction):
            if not name.startswith("_") and name not in vars(BridgeClient) and (tool is None or name == tool):
                print(f"{name}{inspect.signature(fn)}\n{inspect.getdoc(fn)}\n")
