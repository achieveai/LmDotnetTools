#!/usr/bin/env python3
"""Small request-per-process app for /plugins/sandbox-apps/demo.py (read-only mount)."""

import html
import json
import os
import sys
import time
from urllib.parse import parse_qs


def respond(content_type, body, status="200 OK"):
    data = body.encode("utf-8")
    sys.stdout.buffer.write(f"Status: {status}\r\nContent-Type: {content_type}\r\n\r\n".encode("ascii"))
    sys.stdout.buffer.write(data)
    sys.stdout.buffer.flush()


path = os.environ.get("PATH_INFO", "/")
method = os.environ.get("REQUEST_METHOD", "GET")
csrf = html.escape(os.environ.get("SANDBOX_APP_CSRF_TOKEN", ""), quote=True)

if path == "/assets/app.js" and method == "GET":
    respond("text/javascript; charset=utf-8", """
const result = document.querySelector('#result');
const csrfToken = document.querySelector('meta[name="csrf-token"]').content;
document.querySelector('#fetch').onclick = async () => {
  const response = await fetch('/data');
  result.textContent = JSON.stringify(await response.json());
};
document.querySelector('#xhr').onclick = () => {
  const request = new XMLHttpRequest();
  request.open('GET', '/data');
  request.onload = () => { result.textContent = request.responseText; };
  request.send();
};
document.querySelector('#post').onclick = async () => {
  const response = await fetch('/filters', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/x-www-form-urlencoded',
      'X-CSRF-Token': csrfToken,
    },
    body: new URLSearchParams({selection: 'JavaScript'}),
  });
  result.textContent = await response.text();
};
document.querySelector('#stream').onclick = async () => {
  result.textContent = '';
  const reader = (await fetch('/slow')).body.getReader();
  const decoder = new TextDecoder();
  while (true) {
    const {value, done} = await reader.read();
    if (done) break;
    result.textContent += decoder.decode(value, {stream: true});
  }
};
""")
elif path == "/assets/app.css" and method == "GET":
    respond("text/css; charset=utf-8", "body{font:16px system-ui;margin:2rem;max-width:44rem}button{margin:.3rem}pre{white-space:pre-wrap}")
elif path == "/data" and method == "GET":
    respond("application/json", json.dumps({"message": "Hello from a fresh sandbox process"}))
elif path == "/slow" and method == "GET":
    sys.stdout.buffer.write(b"Status: 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n")
    sys.stdout.buffer.flush()
    for number in range(1, 4):
        sys.stdout.buffer.write(f"chunk {number}\n".encode("ascii"))
        sys.stdout.buffer.flush()
        time.sleep(1)
elif path == "/filters" and method == "POST":
    length = min(int(os.environ.get("CONTENT_LENGTH", "0")), 65536)
    form = parse_qs(sys.stdin.buffer.read(length).decode("utf-8"))
    selection = html.escape(form.get("selection", [""])[0])
    respond("text/html; charset=utf-8", f"<h1>Filter saved: {selection}</h1><a href='/'>Back</a>")
elif path == "/" and method == "GET":
    respond("text/html; charset=utf-8", f"""<!doctype html>
<html><head><meta charset="utf-8"><title>Sandbox demo</title>
<meta name="csrf-token" content="{csrf}">
<link rel="stylesheet" href="/assets/app.css"></head><body>
<h1>Sandbox demo</h1><p>Each request runs this program once.</p>
<form method="post" action="/filters"><input type="hidden" name="_csrf" value="{csrf}">
<label>Filter <input name="selection" value="sample"></label><button>Submit form</button></form>
<button id="fetch">Fetch JSON</button><button id="xhr">XMLHttpRequest</button>
<button id="post">JavaScript POST</button>
<button id="stream">Stream chunks</button><pre id="result"></pre>
<script src="/assets/app.js"></script></body></html>""")
else:
    respond("text/plain; charset=utf-8", "Not found", "404 Not Found")
