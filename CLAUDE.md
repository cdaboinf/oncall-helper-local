# Working agreement for this repo

The owner wants to understand and approve each change. These rules override any default habit of "just doing it".

## Ask before changing anything
- Do not write or edit code, config, or docs until the owner has said yes to that specific change.
  Discussion, design, questions and research are not permission to build.
- First say what you intend to change (which files, why, what it affects), then wait for an explicit OK.
- If a decision is open, or answers are unclear or contradict each other, ask. Do not pick an interpretation.
- Work one step at a time, in plain language. Answer questions with answers, not edits.

## Never without an explicit yes at that moment
- `git commit`, `git push`.
- Pushing images to a registry, or anything outside this repo (Claude config, Docker Hub, Auth0, AWS, GitHub).
- Downloading images or models (state name, source and size when asking).
- Approval for one action does not carry over to the next one.

## Be precise, not confident
- Do not assume things exist. Verify read-only, or ask. Quote documented steps; say what is unverified.
- Say what was actually tested and what was not.

## Data and secrets
- This app is meant to run on a company laptop. Real incident data, company chats, code and secrets must never
  be committed or pasted into tests. Use made-up or sanitized data only.
- Never print, log, commit, or ask for secret values.

## Project facts
- A standalone local MCP server (stdio, MCP only) for tracking on-call incidents on the owner's laptop.
- Mirrors the logic of the AWS app (`oncall-assistant-api`) but never changes it; the AWS app is built for AWS.
- Components: this app + MongoDB Atlas Local (vector search) + a local embedding service (`nomic-embed-text`).
- Claude does the incident extraction and reasoning; this server only stores and searches.
