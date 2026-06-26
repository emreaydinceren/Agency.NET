# Agent Console — A Beginner's User Manual

Welcome! This guide shows you how to **use** the Agency Agent Console — the chat program that lives in
`src/Harness/Agency.Harness.Console`. You do **not** need to know how the code works, and you do **not**
need any background in AI. If you can open a terminal and type, you can follow along.

> > **What you'll learn:** how to start the app, chat with it, give it commands, let it use your files, and
> stay in control of what it does.

---

## 1. What is this thing?

The Agent Console is a **chat program that runs in your terminal**. You type a message, and an AI types
back — a bit like texting, but the AI on the other end can also *do things* for you: read a file, run a
command, search through documents you've given it, and more.

> **📘 Note — "AI", "LLM", and "agent"**
>
> - An **LLM** ("Large Language Model") is the part that actually writes the replies. Think of it as a very
>   well-read autocomplete: you give it text, it predicts helpful text back. By itself, it can only *talk*.
> - An **agent** is an LLM **plus a set of abilities** (called *tools*) and some rules about how to use them.
>   The agent can decide, "to answer this, I should read that file first," and then actually do it.
> - The simple formula the project uses is: **`AGENT = LLM + HARNESS`**. The *harness* is all the safety
>   rails, tools, and plumbing around the LLM. This Console app is one such harness with a terminal on top.

So when you chat here, you're not just talking to a chatbot — you're talking to an assistant that can take
actions on your computer. That power is great, but it also means the app will sometimes **ask your
permission** before doing something. We'll cover that in [Section 9](#9-permissions-staying-in-control).

---

## 2. Before you start

You need two things.

### 2.1 The .NET 10 SDK

This app is a .NET program, so you need the **.NET 10 SDK** installed. To check, open a terminal and run:

```bash
dotnet --version
```

If you see a version number starting with `10.`, you're good. If the command isn't found, install the
.NET 10 SDK from Microsoft first.

### 2.2 An "LLM endpoint" the app can talk to

The Console itself does **not** contain an AI brain. It connects over the network to a separate service
that runs the model. That service is called an **LLM endpoint** — basically a web address the app sends
your messages to.

> **📘 Note — what's an "endpoint"?**
> An endpoint is just a URL (web address) that a program talks to instead of a human. The app sends your
> message to that URL and gets the AI's reply back from it.

Out of the box, the app is set up to talk to a tool called **LM Studio** running on a machine at
`http://llm-host.example:1234`. LM Studio is a free desktop program that runs AI models on your own hardware.

You have two realistic options:

1. **Use the machine that's already set up** (if you're on the team's network and that address works).
2. **Point the app at your own model** by editing one settings file — see
   [Section 11, "Changing the settings"](#11-changing-the-settings-appsettingsjson).

If neither the model service is reachable, the app will start but every message will come back as an error.
That's normal and fixable — it just means the app couldn't reach the AI.

---

## 3. Starting the app

From the root of the repository, run:

```bash
dotnet run --project src/Harness/Agency.Harness.Console
```

The first run may take a moment while .NET compiles the code. When it's ready, you'll see a welcome banner:

```text
╔═══════════════════════════════════════════╗
║       Agency  ·  Agent Chat Console       ║
╚═══════════════════════════════════════════╝
Provider : OpenAI
Model    : google/gemma-4-e2b
Type /exit to /quit  ·  Ctrl+C to interrupt a turn
```

You might also see one or two startup lines above the banner, like
`[Agency] Skills: loaded 3 skill(s)...` — that's the app telling you which optional features it found. You
can ignore those for now.

Here's what the banner is telling you:

| Line | Meaning |
|---|---|
| **Provider** | *Which kind of service* is answering you (e.g. `OpenAI`-style or `Claude`-style). |
| **Model** | *Which specific AI model* is being used right now (you can change this later with `/model`). |
| The gray line | A reminder of how to quit and how to interrupt. |

> **📘 Note — "provider" vs. "model"**
> A **provider** is the *company or format* of the service (like a brand of phone). A **model** is the
> *specific AI* doing the work (like a phone model). One provider can offer many models.

Below the banner, the app waits for you at a prompt that looks like a blue arrow:

```text
❯
```

That arrow means: **"your turn — type something."**

---

## 4. Your first conversation

Just type a message and press **Enter**. For example:

```text
❯ Hello! Can you tell me a joke?
```

While the AI is thinking, you'll see a small spinner with the word **"Thinking..."**. When the answer
arrives, it appears next to a green dot:

```text
● Why did the developer go broke? Because he used up all his cache!
  ↳ +18 in, +12 out  9.4 tok/s  [Success]
```

Let's decode that last gray line — it appears after **every** reply and is genuinely useful:

| Piece | What it means |
|---|---|
| `+18 in` | The AI **read** 18 *tokens* of input for this reply (your message + background context). |
| `+12 out` | The AI **wrote** 12 *tokens* of output. |
| `9.4 tok/s` | How fast it wrote — *tokens per second*. Higher is faster. |
| `[Success]` | The reply finished cleanly. Other possibilities include `[Error]` if something went wrong. |

> **📘 Note — what's a "token"?**
> AI models don't read whole words; they read **tokens**, which are little chunks of text (roughly ¾ of a
> word on average). "Hello" might be one token; "unbelievable" might be three. You don't need to count
> them — just know that **in** = how much the AI read, **out** = how much it wrote. They matter because
> most paid models charge by the token, and bigger conversations use more.

Keep typing messages and pressing Enter to continue the conversation. The AI **remembers everything you've
said in this session**, so you can ask follow-up questions like "make it shorter" and it will understand.

---

## 5. Reading the screen: panels and dots

As you use the app, a few visual elements show up. Here's a cheat sheet so nothing surprises you.

### The green dot ● — the AI is talking

Anything next to a green `●` is the AI's written reply to you. It's shown as nicely formatted text —
**bold**, bullet lists, headings, code blocks, and even tables all render properly.

### A bordered box — the AI is *using a tool*

Sometimes, instead of just talking, the agent decides to **use a tool** to get something done. When it
does, you'll see a rounded box like this:

```text
╭─ Calling read_file ─────────────────────────╮
│ # My Project                                │
│ This is the first few lines of the file...  │
╰─────────────────────────────────────────────╯
```

The title (`Calling read_file`) tells you **which ability the agent used**, and the gray text inside is a
short preview of what came back.

> **📘 Note — what's a "tool"?**
> A **tool** is an action the agent is allowed to take beyond just talking — like reading a file, writing a
> file, running a command, or searching your documents. The agent chooses when to use them; you stay in
> control through [permissions](#9-permissions-staying-in-control).

### A red line — something went wrong

If a tool fails, its result shows up in red instead of a box. The agent usually reads the error and tries
to recover, so a red line isn't necessarily the end of the world.

---

## 6. Slash commands: the built-in menu

Besides chatting, you can give the app **direct commands**. These all start with a forward slash `/` and
are handled by the app itself — they **do not** go to the AI.

The easiest way to use them: at an empty prompt, **just press `/`**. A little searchable menu pops up
listing every available command. Use the **arrow keys** to move, type to filter, and **Enter** to pick one.

Here are the commands you'll commonly see:

| Command | What it does |
|---|---|
| `/help` | Shows help information. |
| `/clear` | Wipes the screen **and** starts a fresh conversation (the AI forgets what you said before). |
| `/exit` or `/quit` | Ends the session and closes the app. |
| `/model` | Opens a picker to switch which AI model you're talking to (see [Section 7](#7-switching-the-ai-model-model)). |
| `/dump-context` | Shows you *everything* currently being sent to the AI behind the scenes (see [Section 10](#10-peeking-under-the-hood-dump-context)). |
| `/add-file <path>` | Gives the agent one of your documents to search later (see [Section 8](#8-giving-the-agent-your-own-documents)). |
| `/add-folder <path>` | Gives the agent a whole folder of documents. |
| `/projects-list` | Lists the document "projects" the agent knows about. |
| `/projects-load <name>` | Switches on a project so the agent can search it. |
| `/projects-unload <name>` | Switches a project back off. |

> **Heads up:** the document commands (`/add-file`, `/add-folder`, `/projects-*`) only appear when the app
> is configured with a document search feature turned on. If you don't see them, that feature simply isn't
> enabled in your setup — that's fine, the rest of the app works normally.

You may also see commands named after **skills**, like `/some-skill-name`. We cover those briefly in
[Section 12](#12-skills-and-other-extras).

---

## 7. Switching the AI model (`/model`)

Different models are good at different things — some are faster, some are smarter, some are cheaper. To
switch:

1. Type `/model` (or pick it from the `/` menu) and press Enter.
2. A searchable list appears, grouped by provider. Start typing to filter, use arrows to move.
3. Press Enter on your choice.

You'll see a confirmation:

```text
⎿ Switched to model: google/gemma-4-e2b from client LocalVia-OpenAI-API
```

The switch is **instant** — you don't restart the app, and your conversation so far is kept. The next reply
will come from the new model.

> **📘 Note — where does the model list come from?**
> The available models are listed in the app's settings file (`appsettings.json`). If a model you want
> isn't in the list, an admin can add it there. See [Section 11](#11-changing-the-settings-appsettingsjson).

---

## 8. Giving the agent your own documents

One of the most useful features: you can hand the agent your own files, and it can then **search them by
meaning** to answer your questions. This is great for asking questions about a manual, a codebase's docs,
meeting notes, etc.

> **📘 Note — "semantic search" and "embeddings"**
> Normal search looks for exact words. **Semantic search** looks for *meaning* — so a search for "how do I
> sign in?" can find a document that talks about "logging into your account," even with no shared words.
> The app does this by turning your text into lists of numbers called **embeddings** that capture meaning,
> then finding the documents whose numbers are closest to your question. You don't have to manage any of
> this — just know that's why it can "understand" what you're looking for.

### 8.1 Adding a single file

```text
❯ /add-file ./docs/Home.md
```

The app reads the file, breaks it into searchable pieces, and stores them. You'll see a brief spinner and
then a confirmation of how many pieces were saved. From then on, when you ask a related question, the agent
can find and use that content.

### 8.2 Adding a whole folder

```text
❯ /add-folder ./docs
```

The app will ask which **file pattern** to include (the default is `*.md`, meaning "all Markdown files").
If the folder has a lot of files, it will **ask you to confirm** before doing a big import — a friendly
guard so you don't accidentally load thousands of files.

### 8.3 What's a "project"?

When you add documents, the app may ask where to **file** them, or it may file them automatically. Think of
a **project** as a *labeled box of documents*. You can keep, say, your "handbook" docs in one project and
your "API notes" in another, and load only the box you care about right now.

- `/projects-list` shows all the boxes and whether each is currently switched on ("loaded").
- `/projects-load handbook` switches the "handbook" box on, so searches include it.
- `/projects-unload handbook` switches it back off.

When you have exactly one project loaded, the app is smart enough to just put new documents there without
asking. When the choice is unclear (no projects, or several), it'll politely ask whether you mean *Global*
(available everywhere), *Session* (just this run of the app), or a specific project.

> **📘 Note — "Global" vs "Session" vs project**
> - **Global** documents are always searchable, in every session.
> - **Session** documents live only for as long as the app is open this time; close it and they're gone.
> - **Project** documents belong to a named box you can load and unload on demand.

After you add or load/unload documents, the agent is quietly told "here's the current list of documents you
can search" before your next message — so it always knows what's available without you reminding it.

---

## 9. Permissions: staying in control

Because the agent can take real actions (run commands, write files), the app can **stop and ask you first**.
When it wants to do something that needs your okay, the chat pauses and a box appears:

```text
╭─ Permission required ───────────────────────╮
│ Tool: write_file                            │
│ Input: ./notes.txt                          │
│ Proposed rule: WriteFile                    │
╰─────────────────────────────────────────────╯
Choose an action:
  ▸ Allow once
    Allow always
    Deny once
    Deny always
```

Use the arrow keys and Enter to choose:

| Choice | What happens |
|---|---|
| **Allow once** | Let it do this one action, this one time. |
| **Allow always** | Let it do this *and* stop asking for this kind of action from now on. |
| **Deny once** | Refuse this one time. The app may then ask you to type a short reason for the AI. |
| **Deny always** | Refuse and remember the refusal for this kind of action. |

If you choose a **Deny**, you'll get a chance to type a sentence explaining *why* — the AI reads it and can
adjust its plan. You can also just press Enter to skip the explanation.

Pressing **Escape** on the permission box cancels the whole thing — the agent's pending action is dropped
and you're back at the normal prompt.

> **📘 Note — why does it ask?**
> This is a safety feature. The AI is capable, but it's not perfect, and some actions (like overwriting a
> file) are hard to undo. The permission prompt makes sure **a human approves the risky stuff**. You're
> always the one in charge.

> Some prompts will *not* offer "Allow always" — usually those that come from a special safety rule that's
> designed to ask every time. That's intentional.

---

## 10. Peeking under the hood (`/dump-context`)

Curious what the AI actually "sees"? Type:

```text
❯ /dump-context
```

This prints the full **context** being sent to the model right now: its instructions (the *system
prompt*), the whole conversation so far, and the list of tools it has. This is purely a **read-only peek** —
it doesn't change anything or count as a message. It's a great way to demystify what's going on.

> **📘 Note — "context"**
> The **context** is everything the AI is shown for a given reply: its standing instructions, the recent
> conversation, any documents it pulled in, and the menu of tools it can use. The model has no memory
> beyond what's in the context, which is why `/dump-context` is the honest picture of "what it knows right now."

---

## 11. Changing the settings (`appsettings.json`)

Almost everything about the app is controlled by one file:

```text
src/Harness/Agency.Harness.Console/appsettings.json
```

You edit it with any text editor. Here are the parts a beginner is most likely to touch.

### Point the app at a different model service

Look for the `Agent` section. `DefaultClientName` and `DefaultModel` decide what you talk to on startup,
and each entry under `LLmClients` describes one service:

```json
"Agent": {
  "DefaultClientName": "LocalVia-OpenAI-API",
  "DefaultModel": "google/gemma-4-e2b",
  "LLmClients": [
    {
      "Name": "LocalVia-OpenAI-API",
      "ClientType": "OpenAI",
      "BaseUrl": "http://llm-host.example:1234/v1",
      "ApiKey": "lm-studio"
    }
  ]
}
```

To use *your own* local LM Studio, change `BaseUrl` to wherever your LM Studio is running (often
`http://localhost:1234/v1`), and set `DefaultModel` to a model name your LM Studio has loaded.

### Turn the document-search feature on or off

The document commands depend on the `Embedding` section having a `BaseUrl`. If it's set (it is by default),
the feature is on. Remove or blank it out and those commands disappear.

### Other sections (just so you recognize them)

| Section | Controls |
|---|---|
| `Memory` | Long-term memory across sessions (off by default — see [Section 12](#12-skills-and-other-extras)). |
| `Mcp` | Extra tool servers the agent can connect to (advanced). |
| `Skills` | Where reusable "skills" are loaded from. |
| `Permissions` | The default allow/ask/deny rules for tools. |
| `Retrieval` / `Ingestion` | Fine-tuning for document search (how many results, how files are chunked). |
| `OpenTelemetry` | Where log files are written (default `./logs`). |

> **Tip:** Make a backup copy of `appsettings.json` before your first edit, so you can always get back to a
> working setup.

---

## 12. Skills and other extras

A few optional features you may bump into:

- **Skills** — A *skill* is a saved, reusable instruction the agent can follow on demand (for example,
  "review this code" or "write a commit message"). If the app finds skills, each one becomes a `/command`
  named after it. Run it like any other slash command, and the agent carries out that skill's recipe.

- **Memory** — When turned on in settings, the app can remember useful facts about you **across sessions**,
  not just within one run. It's **off by default** and needs extra services running, so as a beginner you
  can leave it alone.

- **MCP servers** — An advanced way to plug in *more* tools from other programs (for example, a Notion
  connector). If configured, you'll see a startup line like `[Agency] MCP: connected 1 server(s)...` and
  the agent simply has extra abilities. Nothing changes about how you chat.

You don't need any of these to use the app productively. They're there when you grow into them.

---

## 13. Everyday keyboard tricks

A handful of shortcuts make the Console nicer to use:

| Key | What it does |
|---|---|
| **Enter** | Send your message. |
| **Ctrl + Enter** | Add a new line *without* sending — great for writing a multi-line message. |
| **Up / Down arrows** | Scroll through your previous messages (your input history). |
| **Left / Right / Home / End** | Move the cursor along the line you're typing. |
| **Escape** | Clear the line you're currently typing. |
| **`/` (at an empty prompt)** | Open the slash-command menu. |
| **Ctrl + C** | **Interrupt** the current reply (stop the AI mid-thought). Press it **again** when the AI is idle to **quit** the app. |

> **📘 Note — interrupting vs. quitting**
> One Ctrl+C while the AI is working = "stop this answer, I changed my mind." The conversation stays open.
> A second Ctrl+C when nothing is running = "close the app." This two-step design keeps you from quitting by
> accident.

---

## 14. Ending a session

Any of these will close the app cleanly:

- Type `exit` or `quit` and press Enter.
- Type `/exit` or `/quit`.
- Press Ctrl+C when the AI is idle.

On the way out, the app prints a short summary of your session:

```text
Session ended  ·  3 turns  ·  1,234 in, 321 out total
```

That's: how many back-and-forth **turns** you had, and the **total tokens** read and written across the
whole session. Handy for a rough sense of how much work the AI did.

---

## 15. Troubleshooting

| Symptom | Likely cause & fix |
|---|---|
| Every reply comes back as `[Error]` | The app can't reach the model service. Check the `BaseUrl` in `appsettings.json` and make sure LM Studio (or your model service) is running and reachable. |
| The app won't start and mentions memory/embeddings being unreachable | A feature that needs an external service is turned on but the service isn't up. Easiest fix for beginners: set `"Memory": { "Enabled": false }` in `appsettings.json`, or make sure the embedding service address is correct. |
| `/add-file` and friends aren't in the menu | The document-search feature is off. Make sure the `Embedding` section has a valid `BaseUrl`. |
| Replies are very slow | The model is large or the machine is busy. Try a smaller/faster model with `/model`. |
| I picked the wrong model | Just run `/model` again and switch back — no restart needed. |
| The screen looks garbled | Make sure your terminal supports UTF-8 and is reasonably wide. Resizing the window and restarting usually fixes redraw glitches. |
| I want to start a clean conversation | Use `/clear` — it resets the chat and the screen. |

If you're stuck, the app also writes detailed **log files** to the `./logs` folder (next to where you ran
it). Those are mainly for developers, but they're the first place to look when reporting a problem.

---

## 16. Quick glossary

| Term | Plain-English meaning |
|---|---|
| **LLM** | The AI model that writes the replies — a very capable text predictor. |
| **Agent** | An LLM plus tools and rules, so it can *act*, not just talk. |
| **Harness** | All the supporting machinery around the LLM (this app is one). |
| **Tool** | An action the agent can take, like reading a file or running a command. |
| **Token** | A small chunk of text the model reads/writes; usage is measured in these. |
| **Context** | Everything shown to the model for one reply (instructions + conversation + tools). |
| **Provider / Model** | The *kind* of service vs. the *specific* AI doing the work. |
| **Embedding** | A list of numbers representing the meaning of text, used for semantic search. |
| **Semantic search** | Searching by meaning instead of exact words. |
| **Project** | A named box of documents you can load and unload for searching. |
| **Skill** | A saved, reusable instruction the agent can run as a `/command`. |
| **Permission** | The app's "may I?" prompt before the agent does something that needs your okay. |
| **REPL** | "Read–Evaluate–Print Loop" — the fancy name for the type-and-respond chat loop you're using. |
| **MCP** | A standard for plugging extra tool-servers into the agent (advanced). |

---

That's everything you need to be productive. Start the app, say hello, and explore — and remember: when the
agent asks permission, **you're the boss**. Happy chatting!
