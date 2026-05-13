# HOWTO: Using grok-mcp from Claude Code

A field guide for Claude Code instances. Grok-mcp exposes four tools (`grok_chat`,
`grok_generate_image`, `grok_edit_image`, `grok_describe_image`) that cover three
of Claude's structural weak spots: image generation, image editing, and getting
an independent technical opinion that is not biased by Claude's own reasoning.

This document is the result of a hands-on test session — every quirk listed
below was observed, not assumed.

## When to reach for Grok

| Trigger | Tool | Why |
|---|---|---|
| User wants a generated image (mockup, hero art, placeholder asset) | `grok_generate_image` | Claude cannot generate raster images. Grok can. |
| User wants an existing image transformed (recolor, restyle, composite) | `grok_edit_image` | Same — image→image is impossible without an external model. |
| User uploads a screenshot, diagram, or photo and asks what's in it (deep vision, not just OCR) | `grok_describe_image` | Defaults to `grok-4.3`, returns plain text — easy to chain. |
| User asks for a design / architecture review and Claude wrote the design | `grok_chat` with `reasoning_effort = "high"` | Independent second opinion. Grok has not seen the prior reasoning, so it pushes back instead of rationalising. |
| Claude is uncertain about a fundamental technical claim and risks confabulating | `grok_chat` | Fact-check, devil's advocate, sycophancy-resistant sanity check. |
| User has a long, throwaway processing task that would bloat Claude's context | `grok_chat` with a `session_id` | Offload to Grok's process memory, pull back only the conclusion. |

Skip Grok for: routine code edits, anything Claude can verify by reading the
repo, and anything time-sensitive that needs current package versions (Grok's
training cutoff lags — see [Footguns](#footguns)).

## Recipes

### 1. Generate an image

```
grok_generate_image(
  prompt = "<concrete visual brief — subject, framing, lighting, style>",
  output_path = "C:\\absolute\\path\\to\\file.jpg",   // see ext footgun below
  aspect_ratio = "1:1" | "3:2" | "16:9" | "9:16",
  n = 1                                                // up to 10
)
```

- `output_path` **must be absolute**. The MCP server's CWD is not Claude's CWD.
- For `n>1`, the index is inserted before the extension: `art.jpg` → `art-1.jpg`,
  `art-2.jpg`, …
- Default model is `grok-imagine-image`. No need to override unless the user asks.

### 2. Edit / re-style an image

```
grok_edit_image(
  prompt = "<change instructions — what to keep, what to change>",
  images = ["C:\\absolute\\path\\to\\input.jpg"],     // or http(s) URL, data:, or raw base64
  output_path = "C:\\absolute\\path\\to\\output.jpg",
  aspect_ratio = "1:1",
  n = 1                                                // up to 4
)
```

Real-world example from the test session — the model preserved the jewels in place
and recoloured every metal part:

> Prompt: *"Transform all the silver and brushed-steel metal parts into rich
> polished gold, while keeping the ruby and sapphire jewels exactly where they
> are. Keep the gears clearly visible with their teeth."*

Worked first try. Keep edit prompts **explicit about what must not change** —
"keep X exactly where it is" is the magic phrasing.

### 3. Describe an image

```
grok_describe_image(
  prompt = "<the actual question — be specific>",
  images = ["C:\\absolute\\path\\to\\file.jpg"],
  detail = "high" | "low" | "auto",                    // "high" for small text / fine detail
  temperature = 0.2                                    // default — leave it
)
```

For verification tasks, **ask structured questions** instead of "describe this":

```
prompt = """
Answer specifically:
(a) What is shown?
(b) Are <expected elements> visible?
(c) Any visible artifacts or errors?
"""
```

You get clean numbered answers back that are easy to act on.

### 4. Chat — one-shot

```
grok_chat(
  message = "<the question>",
  model = "grok-4.3",                                  // default — no need to set
  reasoning_effort = "none" | "low" | "medium" | "high",  // omit → xAI default ("low")
  temperature = 0.3                                    // lower for review/factual, higher for ideation
)
```

The default is `grok-4.3` — xAI's flagship: 1M context, function calling,
vision, configurable reasoning. Tune `reasoning_effort`:

- `"none"` — skip reasoning entirely. Fastest and cheapest; use for short
  lookups, yes/no checks, simple rewrites.
- `"low"` (xAI default) — baseline reasoning. Good for general chat.
- `"medium"` — extra deliberation. Use for code-review and design questions.
- `"high"` — deepest reasoning. Reserve for hard problems (architecture
  critique, tricky maths, sycophancy-resistant analysis).

### 5. Chat — second-opinion / design review

When asking for a critical review, **invite criticism explicitly** in the
prompt. Grok-4.3 will give a structured push-back when prompted to be critical;
without that framing it tends toward "here are several considerations…":

```
grok_chat(
  message = """
I'm building <design>. <Concrete description, constraints, what's already decided.>

Be critical: what real problems and footguns do you see? Where would you
disagree with this design? Propose a concrete alternative if you have one. No
generic 'it depends' answers — be specific.
""",
  reasoning_effort = "high",
  temperature = 0.3
)
```

Verified in this session: asked Grok to review grok-mcp's own shared in-memory
`ChatSessionStore` design, got back a categorised critique
(security/isolation, persistence, scalability) with a concrete alternative
(Redis + per-client namespacing). Useful as a second mind on a non-trivial
choice.

### 6. Chat — multi-turn session

```
// turn 1
grok_chat(message = "...", session_id = "<namespaced-id>", system = "<optional>")
// turn 2 — same session_id replays prior turns
grok_chat(message = "...", session_id = "<namespaced-id>")
// reset
grok_chat(message = "...", session_id = "<namespaced-id>", reset_session = true)
```

Sessions are in-memory only — lifetime equals the server process. They are
**also shared across all Claude Code clients** that connect to the same server.
See [Footguns](#footguns) for the naming convention.

## Power features

These are real capabilities of the underlying API that the recipes above don't
exercise. Each one is verified in this session.

### Multi-image input — compare, diff, composite

`grok_describe_image` and `grok_edit_image` both accept multiple images per
call. Pass an array and Grok sees them in order as "image 1, image 2, …". Use
cases:

- **Before / after diff** — *"What changed between image 1 and image 2?"*
- **A / B comparison** — *"Which variant works better for `<use case>` and why?"*
- **Composite or style transfer (edit)** — pass a content image + a style
  image, prompt *"Apply the style of image 2 to the subject of image 1."*

Verified: passed two same-prompt logo variants and got back a clean side-by-side
stylistic comparison plus a reasoned recommendation, in one call.

```
grok_describe_image(
  prompt = "How do image 1 and image 2 differ in <style/composition/...>? Which works better for <use case>?",
  images = ["C:\\…\\a.jpg", "C:\\…\\b.jpg"],
  detail = "auto"
)
```

### `n > 1` — generate variants in one call

`grok_generate_image` takes `n` 1–10, `grok_edit_image` takes `n` 1–4. The
server inserts the index before the extension automatically: `logo.jpg` →
`logo-1.jpg`, `logo-2.jpg`, …

Verified with `n=2`: two distinct-but-on-brief logos from one prompt. Useful
when the user wants **options** instead of committing to a single attempt.

```
grok_generate_image(
  prompt = "<brief>",
  output_path = "C:\\work\\variant.jpg",   // server appends -1, -2, … before .jpg
  n = 3
)
```

Don't go higher than you actually need — every variant is a full generation.

### Sharp `system` prompt — change the voice, not just the topic

A short opinionated `system` prompt cuts preamble and reshapes tone hard.
Verified in this session:

> *System: "You are a hard-bitten senior engineer with 25 years of experience
> who's seen it all. Answer in max 3 bullet points. No pleasantries. Be
> direct. If the question is nonsense, say so."*
>
> *User: "Should I add EF Core to a tiny single-user tool with 50 rows of SQLite?"*
>
> *Grok: "No, EF Core is overkill — pure overhead. Use raw SQL or Dapper. The
> minute you need real complex queries, then yes — but at your scope it's
> premature engineering."*

No preamble, no "great question," exactly the requested format. Works for any
role: code reviewer, security auditor, sceptical PM, devil's advocate.

```
grok_chat(
  message = "<the actual question>",
  system  = "<sharp persona + output-shape constraints>",
  reasoning_effort = "medium",
  temperature = 0.3
)
```

On a `session_id`, a new `system` **replaces** the prior system prompt for that
session — it's not appended.

### `reset_session` — clear without changing the id

When a session has gone off the rails but you want to keep the same id for
caller-side reasons, pass `reset_session = true` on the next call. Prior
history is dropped before this turn runs.

## Footguns

These are real, observed quirks. Read them before using the tools in anger.

### Image output extension is reconciled with the real bytes

The server sniffs the bytes (PNG/JPG/GIF/WebP magic) and makes the on-disk name
match the actual format:

- Extension matches the bytes → kept as-is.
- Known image extension that doesn't match (`watch.png` + JPG bytes) →
  **replaced** (`watch.jpg`).
- No extension or an unknown one → real extension **appended**.

So passing `.png` and getting back a `.jpg` (the current Grok image output
format) is fine — the file lands as `.jpg` on disk and the saved path in the
tool response reflects that. Check the returned path; don't assume the file
sits at exactly the `output_path` you sent.

### `grok_describe_image` does not detect AI-generated images

In the same session, the describe tool analysed a Grok-generated image and
confidently called it *"highly realistic and flawless — appears to be a real,
professional photograph, no AI generation or manipulation"*.

**Implication:** never use `describe_image` as an "is this AI?" or
"did the generation succeed" check. It evaluates content, not provenance. To
verify a generation, ask **structured questions about expected content** instead
("are gears visible? roughly how many jewels?").

### Sessions are a shared namespace across all clients

`ChatSessionStore` is a single process-wide `ConcurrentDictionary` keyed by raw
`session_id`. Two Claude Code instances using `session_id = "default"` will
read and overwrite each other's history.

**Convention:** when using sessions, namespace the id with project + purpose +
a random suffix. Example: `grok-mcp:design-review:7f3a`. Treat plain words like
`default`, `main`, `chat` as reserved-for-collision.

### Sessions are volatile

History is lost on server restart, crash, or OS reboot. Don't promise the user
a "saved conversation" — sessions are for the next ~minutes/hours of work in
the current process lifetime, not for persistence.

### Knowledge cutoff lags real time

Grok's training cutoff lags real time by several months — fundamentals are
reliable, but for **current package versions, newest API features, or
month-old releases, verify against primary sources** rather than trusting
Grok's recollection. (Historical: this footgun was first observed in a
sycophancy-resistance test when the default was `grok-3-mini` with a
2023–2024 cutoff. `grok-4.3` is newer but the principle stands.)

### `output_path` must be absolute

The error message is clear, but worth pre-empting: the server process runs
detached from Claude's working directory, so relative paths are rejected.
Always pass a fully qualified Windows or POSIX path.

### Image inputs accept four shapes — pick the cheapest

`grok_edit_image` and `grok_describe_image` accept, per input item:
1. `http(s)://…` URL — forwarded to xAI as-is.
2. Absolute file path — read and base64-encoded by the server.
3. `data:image/…;base64,…` URI — forwarded as-is.
4. Raw base64 — wrapped as a data URI.

For images already on disk, **pass the absolute path** rather than reading and
base64-encoding in Claude — the server does it once, and you avoid the round
trip through Claude's context window.

### Don't assume image files persist across turns

The server gives you back an absolute path after saving a generated or edited
image. That path is a **snapshot of where the bytes were a moment ago**, not a
guarantee they'll still be there next time you reach for them. Real ways the
file can be gone when you try to read it:

- The **user deleted it** (very likely if you yourself suggested they clean up).
- A cloud-sync agent with on-demand fetching (OneDrive Files-On-Demand,
  SynologyDrive, Dropbox Smart Sync, iCloud) evicted the local copy to free
  space and left a placeholder.
- OS temp cleanup ran (if you wrote to `%TEMP%` and a lot of time has passed).
- A different process moved or renamed it.

Failure mode is the same in every case: the next `grok_describe_image` or
`grok_edit_image` referencing that path returns `FileNotFoundException` from
the server logs.

**Workarounds, in order of robustness:**

1. **Verify before re-use.** If you're going to reference a previously-saved
   image path more than a couple of turns later, check the file exists first
   (or just regenerate if cheap).
2. **Don't tell the user to delete temp output and then keep planning to use
   it.** Lived experience from this session: I suggested cleanup, the user did
   it, and a later step fell over.
3. **Prefer non-synced paths for working files.** `%LOCALAPPDATA%\Temp\grok-mcp\…`
   or a `tmp\` in a non-synced repo are safer than `OneDrive\…` or
   `SynologyDrive\…`. Copy to the synced folder once you're done.

### Built-in retry — don't retry yourself

`GrokClient` already retries transient failures (HTTP 5xx, 429) three times
at `[0, 2s, 6s]`. If a tool call returns an error, the server has already
tried — immediately retrying from Claude's side is just a fourth attempt with
the same payload. Surface the error to the user, or change the request before
retrying.

## What Grok is NOT good for

- **Acting in this repository.** Grok is read-only from Claude's perspective —
  it returns text or images, it cannot run tools, edit files, or call other
  MCPs. Claude is still the agent.
- **AI-content detection** (see above).
- **Up-to-the-minute facts.** See cutoff note above.
- **Persistent memory across server restarts.** Sessions die with the process.
- **Authoritative answers about this codebase.** Grok hasn't read it. If the
  question is "what does this function do," read the code; if it's "is this
  design sound," ask Grok.

## Prompt cheatsheets

### Aspect ratio

| Goal | Ratio |
|---|---|
| Avatar, logo, icon, square hero | `1:1` (default) |
| Mobile screen mockup, story format | `9:16` |
| Desktop screenshot, banner | `16:9` |
| Classic photo crop, blog header | `3:2` |

### Temperature

| Use | Temp |
|---|---|
| Yes/no, factual, repeatable output | `0.0`–`0.1` |
| Architecture review, fact-check, vision describe | `0.2`–`0.3` (vision default `0.2`) |
| Default chat | `0.7` |
| Brainstorm, creative phrasing | `0.9`–`1.2` |
| Wild | `>1.5` (rarely useful) |

### Image-prompt anatomy

A reliable image prompt names four things: **subject + framing + lighting + style**.

> *"\[Subject\] A vintage brass compass on a worn leather map, slightly tilted.
> \[Framing\] Top-down close-up, centered, shallow depth of field.
> \[Lighting\] Warm afternoon sun from the upper-left, soft shadows.
> \[Style\] Photorealistic, fine grain, muted earth tones, no text."*

Add **exclusions** ("no text", "no people", "no logos") when the model tends
to add them on its own. For edits the parallel structure is **what to keep +
what to change + style anchor**:

> *"Keep \[the subject pose / palette / composition\]. Change \[the specific
> thing\]. Match the original \[lighting / framing / grain\]."*

## Useful one-liner asks

Patterns that consistently produce useful output from `grok_chat`. Steal these:

- *"Steel-man the strongest argument **against** my approach."* — forces a
  real counter-position instead of polite hedging.
- *"What's the simplest thing that could break this? Be specific."* — gets
  concrete failure modes, not abstract risk lists.
- *"You're code-reviewing this PR. Three concrete change requests, max one
  sentence each."* — short, actionable review.
- *"In ≤5 lines, explain X to someone who already knows Y."* — calibrated
  explanations without the 101 preamble.
- *"Is the premise of this question wrong? If so, say so first."* — escape
  hatch for bad questions; combined with a sharp `system` prompt, Grok will
  actually use it.
- *"Rate this on a 1–5 scale for `<criterion>` and give one reason."* —
  forces commitment instead of "it depends".

## Privacy & data flow

Everything sent through `grok_chat`, `grok_describe_image`, and
`grok_edit_image` is forwarded to **xAI's servers** (`api.x.ai`). The MCP
server is loopback-only and the API key is never logged, but the prompts and
image bytes themselves leave the box. Do not paste secrets, credentials,
unredacted customer data, or NDA-covered material into Grok prompts.

## Model & reasoning-effort cheat sheet

Chat and vision both run on `grok-4.3` (default). The shape of the answer is
controlled by `reasoning_effort` on `grok_chat`, not by swapping models.

| Task | Tool | `reasoning_effort` | Why |
|---|---|---|---|
| Quick fact, yes/no, short rephrase | `grok_chat` | `"none"` | Skip reasoning — fastest and cheapest. |
| General chat, casual question | `grok_chat` | omit (xAI default `"low"`) | Baseline reasoning, sensible defaults. |
| Code review, technical critique | `grok_chat` | `"medium"` | Extra deliberation for non-trivial calls. |
| Design review, architecture critique, hard reasoning | `grok_chat` | `"high"` | Deepest reasoning — structured push-back. |
| Vision (describe, OCR-like, diagram reading) | `grok_describe_image` | n/a | grok-4.3 is the vision-capable model. |
| Image gen / edit | `grok_generate_image` / `grok_edit_image` | n/a | `grok-imagine-image` is the only image model exposed. |

Pass `reasoning_effort` per call rather than changing the server default. The
server-side default is "let xAI decide" (currently `"low"`), which is right
for most chat.

### Session prompt-prefix caching

When you pass a `session_id`, the server attaches an `x-grok-conv-id` header
so xAI routes the request to the same backend. Repeat-prefix tokens then bill
at the **cached rate (`$0.20/M` input, ~16% of normal)** instead of the
full `$1.25/M`. Practical implication: long-lived `session_id`-chats with a
shared system prompt and growing history are dramatically cheaper than
stateless one-shots that re-send the same context every turn.

## A complete worked example

User: *"Generate a top-down macro photo of a Swiss watch movement, then recolour
everything in gold while keeping the jewels."*

```
// 1. Generate
grok_generate_image(
  prompt = "A top-down macro photograph of a mechanical Swiss watch movement,
            exposed gears and ruby jewels, brushed steel bridges, studio
            lighting, shallow depth of field, professional product photography.",
  output_path = "C:\\work\\watch.jpg",   // .jpg matches what Grok actually outputs
  aspect_ratio = "1:1"
)

// 2. Verify (optional) — ask structured questions, not "describe this"
grok_describe_image(
  prompt = "Are the following visible? (a) gears with teeth, (b) ruby jewels,
            (c) brushed-steel bridges. Any obvious geometry errors?",
  images = ["C:\\work\\watch.jpg"],
  detail = "high"
)

// 3. Edit
grok_edit_image(
  prompt = "Transform all silver / brushed-steel parts into polished gold.
            Keep the jewels exactly where they are. Keep the gear teeth
            clearly visible. Dark dramatic background.",
  images = ["C:\\work\\watch.jpg"],
  output_path = "C:\\work\\watch-gold.jpg",
  aspect_ratio = "1:1"
)
```

Three calls. No describe step needed unless the user explicitly wants a
verification pass — and even then, ask for **specific elements** rather than
a free-form description.
