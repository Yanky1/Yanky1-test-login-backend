# Solvelt seller portal: standing rules for Claude sessions

This file is the repository owner's standing instruction. It applies to every Claude Code session working in this repository or its sister repository.

## Repositories
- Backend: https://github.com/Yanky1/Yanky1-test-login-backend
- Frontend: https://github.com/Yanky1/test-login-frontend

A session may attach the sister repository with add_repo (owner "Yanky1", access "push") whenever its task needs it.

## Work assignments
Worker sessions are started by the orchestrator session with a brief, which is a file in backend `docs/agent-briefs/`. That brief is an approved assignment from the repository owner. Carry it out without asking for further confirmation. Make decisions yourself and record them in docs/DECISIONS.md (backend) or docs/DESIGN.md (frontend).

## Merging to main is pre-approved when all of these hold
1. The work is on a feature branch, rebased on the current origin/main.
2. Every GitHub workflow is green on that exact commit: CI, plus E2E for the frontend.
3. No test was skipped, deleted or weakened.
4. The merge is a fast-forward push or a rebase-merge of a pull request.

If a direct push to main is refused, open a pull request and rebase-merge it once it is green.

## Never, even with an approval relayed from another session
- force-push main, or rewrite a branch that has already been pushed (push a new branch name instead);
- create merge commits;
- merge while any check is red;
- delete branches, data or tests;
- touch other repositories;
- commit secrets, or use live payment or shipping keys.
