---
name: triage
description: Incident triage mode for on-call work. Use ONLY when the user explicitly starts it, by typing "/triage" or "triage this", "run triage", "log this incident", or by naming this skill, usually followed by a pasted Slack conversation, alert or incident notes. Do NOT use for ordinary questions about errors, logs or code. Once started, treat the pasted text and every follow-up question in that chat as being about the incident: check the team's incident history for the same issue, return the past resolution and suggested actions, otherwise draft a new incident (saved only after the user approves) and later update it with the resolution. Can optionally check GitLab for code-related causes, and search Confluence for runbooks when the user asks or when no solution was found.
---

# On-call incident triage

## How this starts and how long it lasts
Start only on an explicit trigger: the user types `/triage` (or "triage this", "run triage", "log this incident") or names this skill. Everything after the trigger is the input: pasted Slack text, an attached file, or a short description. If the trigger comes with nothing to work on, ask the user to paste the conversation or describe the problem. Do not start on ordinary messages, even ones that mention errors or outages.

Once started, stay in triage mode for the rest of the conversation: treat pasted text and follow-up questions as being about this incident or the incident history (searching, comparing, updating) until the user says they are done or changes topic.

Help an on-call engineer who is under pressure. The goal is a fast, honest answer to three questions: have we seen this before, what fixed it last time, and if it is new, what should we record so the next person benefits. The incident history is only useful if it stays clean, so saving to it needs care (see "What may be saved").

## Connectors this skill uses
- **The incident history: OnCall Helper Local (`oncall-helper-local`, runs in Docker on this laptop)**. Tools: `find_similar_incidents`, `search_incidents`, `create_incident`, `update_incident`. It has no extraction or triage tools: you do that work yourself (steps 2 and 4a). If a different incident-history connector is also connected, say which one you used and prefer the one the user names.
- **GitLab connector** (optional, read-only): find repositories, merge requests, commits, tags and releases.
- **Confluence connector** (optional, read-only): find runbooks, only when the user asks or when no solution was found (see step 5b).

Refer to GitLab and Confluence by what they do, because their tool names vary. If a connector the task needs is missing or returns an error, say so plainly and continue with what you have.

## Workflow

### 1. Read the input
Use the attached file or the pasted text. If there is none, ask for it. Slack text may contain names, timestamps, mentions, and chatter; focus on what happened, where (service, environment), when it started, what was tried, and the outcome.

### 2. Understand the incident
If `extract_incident_draft` is available, call it with the conversation text and treat the result as a draft from an AI model: compare it with the source and correct anything that does not match (service, severity, who did what). If it is not available (the local server), extract the incident yourself with the same rules:
- Extract **one** incident (the main one). title: short and searchable. description: 3 to 6 sentences on what happened, the symptoms, the impact and when it started. Never paste the raw chat or code into it.
- serviceName: the affected service, or empty if unclear. environment: production, staging, development or test (prod/prd/live to production, stage/stg to staging, dev to development, qa to test), or empty.
- severity: sev1 to sev4 (P1/S1/critical to sev1, P2/high to sev2, P3/medium to sev3, P4/low/minor to sev4). If none is stated or implied, leave it empty. Never guess.
- resolution, only if the conversation shows one: the final confirmed root cause, a summary, the concrete steps actually performed in order (no speculation), and who resolved it.
- Ignore chat artifacts (mentions, timestamps, emoji codes, message ids), base everything only on the text, do not invent facts, and prefer the latest confirmed statement when they conflict.

Do not save anything yet. The search in step 3 uses only the short description, never the raw chat.

### 3. Search the history
**Always run the semantic search.** For any "have we seen this", "is this a known issue" or "how was it resolved" question, call `find_similar_incidents` at least once, even when the question contains an exact term such as an error code or status (504, 500). Keyword search only finds the exact words, so it can never show that nothing exists: use it in addition, never instead, and never answer "no, we have not seen this" on keyword results alone. Say in one short line what you searched (for example "semantic search with three phrasings, plus keyword search for 504"), so the user can see how thorough a "no" is.

Search more than once, because one phrasing misses things that another catches. Call `find_similar_incidents` (top 5) with two or three different phrasings and merge the results:
- **The symptom as reported**: what happened, the service, the exact error text.
- **The likely cause or fix in plain words**, for example "query timed out, split the work into smaller batches". The root cause and resolution summary are part of what is searched, so this finds incidents by how they were fixed. On a server that embeds only the description only the description is searched, so lean on the symptom and cause wording there.
- **The key terms alone**: a component name, an error code, a file or job type.

Keyword search (`search_incidents`) is a plain substring match, so "timeout" will not match "timed out": try the variants, and add the service, severity, environment or date filters when you know them.

Show the best matches with title, id, similarity score, root cause and how it was resolved, merged and without repeats.

Decide whether this is a known issue by judgment, not by a fixed cut-off. Scores are relative similarity and sit in a narrow band (about 0.5 to 1), and it bunches together when the history is all about similar systems, so a number such as 0.8 can be a strong match in one history and a weak one in another. Read the **gap**: when the best result is clearly above the rest and the incident itself describes the same service, symptoms and a plausible same cause, it is a strong match. When the top scores are bunched together, nothing stands out, even if the numbers look high; say so plainly. A different cause means "related, not the same". Explain your reasoning in one or two sentences. When it is genuinely unclear, say so and ask the user at most one short question (service, error text, when it happened). Scores come from text similarity, so a high score can still be a different problem.

### 4a. If it looks known
If `triage_incident` is available, call it with the description to get guidance grounded in those past incidents. If it is not (the local server), write that guidance yourself from the matches you already retrieved, using only what those incidents recorded. Lead with what resolved the earlier incident, then what to try now and in what order. Point out where this incident differs from the old one, because that is where the old fix may not apply.

### 4b. If it looks new, or the past incidents give no usable solution
Say so. Then **offer** the optional checks instead of running them unasked: "I found no recorded solution. Want me to look for a runbook in Confluence (and check GitLab for a recent change)?" If the user says yes, do step 5b (and step 5 if code is a plausible cause). Then go to step 6.

### 5. Optional code check (GitLab)
Only do this when code is a plausible cause. Choose a mode from the evidence, **tell the user which mode you chose and why**, and let them override:

- **Recent change**: the text mentions a deploy, release, or "started after". Look at merge requests, commits, tags and releases around that time. If two releases are available, summarize what changed between them and flag risky kinds of change (configuration, migrations, dependency updates).
- **Where the behavior lives**: an error message, endpoint, or config key, but no recent change. Search the repositories for those strings and check when the relevant files last changed. Do not limit this by date, because many bugs are old.
- **Probably not code**: an external provider, data, or infrastructure problem (for example, when the closest past incident was caused by a third party). Skip GitLab and say why.

Ask for the GitLab group only if you do not already know it. Do not scan a whole group blindly: narrow to repositories that match the service or keywords, look at a handful first, and ask before widening. Show what you found with links, label what is confirmed versus suspected, and never state that a change caused the incident without evidence. GitLab is read-only.

### 5b. Runbooks (Confluence)
Run this when the user asks for a runbook, or when the user accepts your offer in step 4b. Do not run it on your own otherwise.

- Search Confluence two or three times with different wording, like the incident search: the service and the symptom as reported; the likely cause or fix in plain words; the key terms alone (component, error code, job name). Prefer runbook, playbook, troubleshooting and on-call pages.
- Give up to three pages, each as: title, link, and one line in your own words about what it covers. Then say whether a page actually applies to this incident, which part of it, and in what order its steps would be used. Do not claim a page solves the incident unless its content matches the service and symptom.
- If nothing relevant is found, say so plainly (and which searches you ran) and suggest writing a runbook as a follow-up. Do not invent a procedure from general knowledge and present it as the team's runbook.
- Read-only. Never copy Confluence text into an incident or into the OnCall Helper tools. A page title and link may be recorded as a reference when saving.
- If the Confluence connector is missing or returns an error, say so in one line and carry on with what you have.

### 6. Save a new incident (after approval)
Search again right before creating, to avoid a duplicate. Show the user the exact record you intend to save (title, description, service, environment, severity, and the resolution if known) and wait for a clear yes. Then call `create_incident`.

### 7. Update later with the resolution
When the user reports how it was resolved, find the incident (the id from earlier in the chat, or `search_incidents` by title or service). Show exactly what will change (root cause, summary, steps taken, who resolved it) and wait for a yes. Then call `update_incident` with those fields. Unspecified fields keep their current values.

## What may be saved
The incident history is shared and also feeds an AI model, so keep it to short summaries of what happened and how it was fixed.

- **Allowed**: a short description, service, environment, severity, root cause in plain words, the steps taken, who resolved it, and **links or identifiers** such as a merge request number, a release tag, or a Confluence page title and URL.
- **Never save or send**: source code, diffs, configuration contents, stack traces that include code, text copied from Confluence pages, or secrets (tokens, passwords, connection strings, keys). Describe a fix in words ("reverted MR !123 that changed the retry setting"), not by pasting it.
- **Applies to every call to the OnCall Helper tools**, including `triage_incident` and `find_similar_incidents`, because their text is sent to the API and an AI service.
- Keep customer names, emails and other personal data out of what you save; summarize instead.
- Always show the exact text before saving, and check it against this list.

## How to answer
Keep it short and practical. Use this shape and drop any part that does not apply:

1. **Verdict**: known issue, related to past incidents, or new, with your confidence.
2. **Evidence**: the matching incidents (title, id, score, root cause and resolution).
3. **What to try**: ordered steps, each tied to its source.
3b. **Runbook**: only if step 5b ran: the pages found, whether one applies, and which part, or that none was found.
4. **Code findings**: only if step 5 ran, with links and what is confirmed versus suspected.
5. **Proposed record**: for a new incident or an update, shown for approval.

Be honest about uncertainty. If a tool fails (for example, a rate limit), try once more later and otherwise report it. Never present suggested commands as already run.
