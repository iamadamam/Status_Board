# CLAUDE.md

Status Board is a portable Windows desktop dashboard (WPF, .NET 10) showing the public status of cloud, SaaS and
everyday services, plus the user's own website, ping and LibreNMS checks. It reads only public status pages and feeds:
outbound GET requests, no accounts, no telemetry. Features, the project layout and how to add a source type are in
`README.md`; security design notes are in `SECURITY.md`.

## Rules

### Work on a branch

Start every piece of work on a fresh branch off `main` (`git switch main && git pull && git switch -c <type>/<short-name>`,
e.g. `fix/ticker-pause`, `feat/grafana-provider`, `docs/readme-tests`). Commit to that branch and bring it into `main`
through a pull request. `main` changes only by merging a PR: never commit or push to it directly.

### Public repo: commit only what is safe to publish

Everything committed is public forever, even after deletion from history. Before every commit, read
`git diff --staged` and confirm it holds only code, docs and sanitised test samples. Keep out:

- secrets: API keys, tokens (e.g. LibreNMS), passwords, certificates, `.env` files;
- personal data: real names, email addresses, usernames, home-folder paths (`C:\Users\<name>\...`), internal
  hostnames and IP addresses;
- local app data: `statusboard.json`, `profiles.json`, `profile-*.json` and `logos\`. The app writes these next to
  its exe, so they appear in `bin\` and `artifacts\` (both gitignored) and anywhere a copy of the app runs.

New files in `tests/StatusBoard.Tests/Samples/` must be captured from **public** endpoints or written by hand; scrub
anything account- or person-specific first. Use placeholders like `example.com` and `192.0.2.1` in tests and docs.

### Explain every change in plain English

The repo owner is learning. After each change, explain:

- **what** changed (files and behaviour),
- **why** it was needed,
- **how** it works, in plain English.

Define each technical term the first time it appears. Teach the concept, not just the diff.

## Build, run and test

Requires Windows 10/11 and the .NET 10 SDK. There is no solution file at the root, so always name the project:

```powershell
dotnet test tests/StatusBoard.Tests          # unit tests: saved responses in Samples/, no network
dotnet run --project src/StatusBoard         # run the app from source (add -- --tv for TV mode)
.\build.ps1                                  # tests + release build: artifacts\StatusBoard\ and the zip
```

Plain `dotnet test` from the root fails with `MSB1003` (no project or solution file found).

- Run the unit tests after every code change and report the result.
- Live tests hit every real vendor over the internet. Run them only when asked:
  `$env:STATUSBOARD_LIVE='1'; dotnet test tests/StatusBoard.Tests`.
- The UI self-test (`--selftest`) and the snapshot mode (`--snapshot`) are in the README's Building section. The
  self-test rewrites settings files, so run it on a copy of the build as the README shows.
- `tools/check-footprint.ps1` proves the app writes nothing outside its own folder. Run it after changing anything
  that touches files, logos or settings.

## Conventions

- `StatusBoard.Core` stays UI-free and package-free; all WPF code lives in `src/StatusBoard`.
- Parsers are static `Parse(...)` methods tested against a saved file in `Samples/`.
- Network behaviour is deliberately conservative (timeouts, retries, size caps, no redirects with tokens, validated
  certificates). Keep it that way and mention any change to it explicitly.
- User-facing changes go in `CHANGELOG.md` under an `[Unreleased]` heading (Keep a Changelog format).

## Agent skills

### Issue tracker

Issues live in GitHub Issues on iamadamam/Status_Board (via `gh`). See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-role vocabulary: needs-triage, needs-info, ready-for-agent, ready-for-human, wontfix. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `GLOSSARY.md` + `docs/adr/` at the repo root. See `docs/agents/domain.md`.
