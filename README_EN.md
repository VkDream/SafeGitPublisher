# SafeGitPublisher — Safe GitHub Publishing Assistant for Windows

> [中文文档 README.md](README.md)

A Windows desktop tool (C# / WPF / .NET 10 / x64 / MVVM) that runs **mandatory safety preflight checks** before every commit & push: secret scanning, sensitive files, large files, total repo size, author identity, remote validation, image privacy and build verification — and collapses "check → commit → push" into one protected publishing flow.

Built for developers who want the power of Git without the risk of leaking API keys, pushing 2 GB of test images, or committing build artifacts by accident.

## Why

Real incidents this tool was designed to prevent (all happened in the field):

- A first-time push contained **805 files / 2.05 GB** — 113 uncompressed BMP test images (14 MB each), a commercial vision library DLL, and Visual Studio build artifacts. Every single file passed per-file limits; only a **total-size gate** could catch it.
- `ghp_...` tokens hard-coded in config files, pushed to a public repo.
- Debug screenshots containing customer names and server addresses committed unnoticed.
- `git push` that "succeeded" while the network died mid-flight, leaving an ambiguous remote state.

## The 13 Preflight Gates

| # | Gate | Behavior |
|---|------|----------|
| 1 | Git environment | git CLI missing → Blocked |
| 2 | Repository detection | Not a repo → Blocked (one-click `git init`) |
| 3 | Worktree status | Merge conflicts → Blocked |
| 4 | .gitignore | Missing recommended rules → Warning (one-click generate, **append-only, never overwrites**) |
| 5 | Sensitive files | `bin/obj/publish/.vs`, `*.db`, `.env`, `secrets.json`, `*.pfx/*.key/*.pem`, local AI tool metadata (`.claude/`, `.serena/`) → Blocked; already-ignored ones shown as "safely ignored" |
| 6 | Secret scan | `github_pat_` / `ghp_` / `sk-` / `AKIA` / `Bearer` tokens → Blocked; plaintext credential assignments (High) → hard-blocked; intranet IPs → Warning. Output always redacted |
| 7 | Large files | >10 MB Warning, >50 MB high Warning, >100 MB (GitHub hard limit) → Blocked |
| 8 | **Total repo size** | Pending content >500 MB → Warning, >1000 MB → Blocked (configurable), with per-extension Top-N breakdown. Catches "many medium files" that per-file gates miss |
| 9 | Git author | Mismatch with recommended identity → Warning (one-click apply, repo-local config only) |
| 10 | Remote | No origin → Warning (push disabled); malformed URL → Blocked. GitHub-only (HTTPS / SSH) |
| 11 | Branch | detached HEAD → push disabled |
| 12 | Image privacy | New/modified images require a manual desensitization confirmation before push |
| 13 | Build | .NET build failure → push blocked (uses **isolated temp output** via `dotnet build --artifacts-path`, never touches your running local build outputs) |

## Safety Contracts

- **Zero Change Gate**: with 0 committable changes, commit/push is disabled no matter what the message says. The confirm dialog cannot even open.
- **Origin-first**: before any `git add`, the exact origin push URL and branch are probed via git.exe; if the network/auth path is unavailable, no local commit is created.
- **Locked-OID push**: push uses the explicit full OID refspec `<full-oid>:refs/heads/<branch>` against the exact validated URL — never re-resolves HEAD mid-flight.
- **Index/outgoing re-scan**: after `git add --all`, staged blobs are re-scanned from raw bytes (content-based binary detection, not extension trust); the scanned index tree must match the actual commit tree, or publishing is refused (hook-tamper proof). Push additionally scans the real outgoing history (deduplicated blobs).
- **Push recovery**: if push state is unknown (network died), the tool refuses blind retry and offers a dedicated "verify & upload existing commit" flow bound to the locked OID, branch and remote fingerprint.
- **No dangerous commands, ever**: force push, rebase, `reset --hard`, `clean -fd`, filter-repo, branch -D are never executed automatically.
- **Fail closed**: any unreadable file, incomplete scan, or unexpected git output blocks publishing instead of skipping the check.

## UI Features

- Recent projects (10) with automatic check on selection
- 13-gate result list with ✅ / ⚠ / 🚫 and one-click fixes
- Change list (status / size / risk) + detailed report dialog
- Commit type shown in Chinese, stored as standard Conventional Commits prefixes (`feat:` / `fix:` / `docs:` / ...)
- Two publish buttons: **Commit Only** / **Commit & Push**, both behind a final confirmation page
- **First-publish wizard**: git init → .gitignore → identity → origin → full check → confirm → publish
- Settings dialog (large-file thresholds, total-size thresholds, build toggle, image confirmation toggle, recommended author)

## Build & Test

```
dotnet build SafeGitPublisher.slnx -c Release
```

- Unit tests: `tests/SafeGitPublisher.Tests` — zero-dependency console runner, 156 cases (incl. GUI smoke host)
- E2E tests: `tests/SafeGitPublisher.E2E` — 34 scenarios against real git.exe in %TEMP% repos (never touches user repos or global git config)

## Limitations

- GitHub remotes only (HTTPS / SSH URL parsing); other hosts get a non-blocking notice.
- Image desensitization is a manual confirmation; the tool does not perform OCR/content recognition.
- Build gate supports .NET projects (csproj/sln/slnx); other languages are skipped gracefully.
- No credential management, no GitHub REST API calls — push is performed by git.exe and your own git credential manager.

## License

Internal use. See repository for details.
