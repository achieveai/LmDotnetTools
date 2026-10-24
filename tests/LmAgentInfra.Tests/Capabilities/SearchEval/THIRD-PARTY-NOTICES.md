# Third-party notices: capability search eval set

`corpus.json` holds the names and descriptions of skills, agents and plugins taken from the plugin
repositories below. `queries.jsonl` was written by hand against it. The data is test input only; it
is not shipped in any package. `corpus.json` was extracted with `extract-corpus.mjs` (kept with the
eval notes outside the repo) and regenerates byte-identical from these commits
(sha256 `95739cf1288b62e7ee78ea06b30f41f8af4a89c6e3428ab462cd72f4297a4881`).

| `marketplace` in corpus.json | Source repository | Commit | Licence |
|---|---|---|---|
| `knowledge-work-plugins` | https://github.com/anthropics/knowledge-work-plugins | `33eeda1e8fd856455b05479d13a0f85b5f803744` | Apache License 2.0 |
| `superpowers` | https://github.com/obra/superpowers | `bb92a77741419a4ab5f06e711a283343f1ada0c3` | MIT |
| `ClaudePlugins` | https://github.com/gautam-achieveai/ClaudePlugins | `4f6533a95626591e967787373bc65d199aeb29ad` | Owned by the repository owner |
| `sandbox-plugins` | https://github.com/achieveai/sandbox-plugins | `d1b0e187d4c6960ac191a36d77cbe1aa153be06a` | Owned by the repository owner |

## knowledge-work-plugins (Apache License 2.0)

Descriptions from knowledge-work-plugins are used under the Apache License, Version 2.0. A copy of
the licence is in `LICENSE-Apache-2.0.txt` beside this file, taken unchanged from
https://www.apache.org/licenses/LICENSE-2.0.txt. The repository has no `NOTICE` file at the commit
above. The descriptions are copied unchanged.

## superpowers (MIT)

```
MIT License

Copyright (c) 2025 Jesse Vincent

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
