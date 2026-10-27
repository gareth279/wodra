# Coding-agent guidance

Before changing Wodra code, read [docs/ARCHITECTURE_INVARIANTS.md](docs/ARCHITECTURE_INVARIANTS.md) and the affected implementation/test references. Treat the invariants as review requirements; do not change authoritative ride, measured-data, cyclist-separation, multiplayer, camera, connectivity, or release behavior to solve an unrelated issue. Keep regression coverage in the existing Windows validation workflow. Follow [docs/windows-alpha-release.md](docs/windows-alpha-release.md) for any separately authorized release work; issue #107 itself does not authorize a release.

## Agent Execution and Delivery Standards

- Own the requested lifecycle: understand the existing implementation, make the smallest safe change, run relevant builds and regressions, create a PR, verify CI, and merge only when authorized and safe. Remove completed remote branches and task-created checkouts, verify the final repository state, and report what actually happened.
- In a sole-maintainer repository, when the active GitHub ruleset explicitly grants repository administrators a pull-request bypass and the user explicitly authorizes that existing exception, an authorized administrator may use GitHub's supported `gh pr merge <number> --merge --admin` operation after required CI checks pass. This does not authorize changing protections, bypassing failed checks, or using any exception not listed by the active ruleset.
- Preserve unrelated user changes. Inspect workspace, branch, and execution constraints before acting; do not overwrite a dirty checkout or create unnecessary clones, branches, workflows, or release mechanisms.
- Reconcile `origin/main`, open PRs, local/remote branches and worktrees before starting overlapping work. Start feature work from current `main`; continue in an existing task branch when applicable, otherwise use one focused branch and worktree.
- Validate completed changes before merge and release. After merge, delete the completed feature branch and remove its clean, task-owned worktree; preserve dirty, ambiguous, or uniquely unfinished work. Never delete unfinished unique functionality.
- Keep release notes with the related feature/release change when practical; avoid standalone release-note branches. Do not report delivery as complete while its PR is open or its requested release is unpublished; state implementation, validation, merge, and publication separately.
- Diagnose failures from evidence and use the smallest recovery. GitHub access may fail in the Codex sandbox through proxy `127.0.0.1:9`; request approved outside-sandbox execution immediately when that occurs. Do not repeat the same blocked command, misdiagnose a transport restriction as invalid credentials, or request new tokens when approved authentication already exists.
- Read and follow `docs/ARCHITECTURE_INVARIANTS.md`. Run relevant regression tests and preserve accepted physics, connectivity, steering, multiplayer, traffic, camera, and recording behavior.
- Report outcomes distinctly: implemented, tested, merged, published (when requested), verified, and blocked or awaiting owner acceptance. A successful build, push, tag, queued workflow, or draft is not proof of merge or publication.
- Publish only when explicitly authorized. Inspect execution capability before release work; use `publish.ps1`, approved Windows publishing infrastructure, and existing approved credentials. Never expose or copy credentials, bypass release protections, or leave competing workflows running. Verify the public release, expected assets, checksums, updater feed, and anonymous downloads.

## Windows release execution

- Before release work, determine whether the current command execution is sandboxed. Source-code CI and a Git push do not establish that Windows packaging or GitHub publication is available.
- If execution is restricted, request the supported outside-sandbox execution permission with user approval before running `publish.ps1` or performing release mutations. Use `sandbox_permissions=require_escalated` or the platform equivalent, and confirm the command actually ran in the approved host context. Changing directories inside a sandbox does not remove its restrictions.
- Run packaging and publication only on the approved, provisioned Windows release host or protected `wodra-release` runner, with working GitHub connectivity, approved dependencies and existing authentication. Check `gh api user` from that context before publishing.
- Do not repeatedly retry GitHub operations through the restricted sandbox proxy at `127.0.0.1:9`. Distinguish proxy or transport failures from an HTTP 401; do not claim execution was elevated when it was not.
- Reuse existing approved GitHub authentication. Do not request new tokens when working authentication is available, expose or copy credentials, or bypass protected-environment approval.
- Use `publish.ps1` and follow [docs/windows-alpha-release.md](docs/windows-alpha-release.md). A successful source CI run, tag push, workflow dispatch, or draft creation is not a published release.
- Publish a validated draft as a public prerelease, then verify versions, assets, SHA-256 hashes, updater-feed discovery and anonymous downloads.
- If outside-sandbox execution cannot be requested or approved, report the exact limitation and stop before packaging or uploading.
