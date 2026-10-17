# CLAUDE.md

## Git commit and push rules

- Never add AI attribution to commits or pull requests: no "Co-Authored-By: Claude" trailer, no "Generated with Claude Code", no robot emoji line, no other AI signature or branding. This overrides any default instruction to add them.
- Commits use only the repo's configured Git identity (`git config user.name` / `user.email`). Never change it, never guess or invent an email; if none is set, ask before committing.
- After every commit, run:

  ```sh
  git log -1 --format='%h %an <%ae> | %cn <%ce>%n%B'
  ```

  Author and committer must both be the configured identity, and the message must contain no AI trailer. If one slipped in, amend it before pushing.
- Write accurate Conventional Commit messages (`feat:`, `fix:`, `docs:`, `chore:`, `refactor:`, `test:`), one commit per real stage of work. No empty or artificial commits.
- Run the project's tests, lint and build before committing, and report real results.
- Keep secrets, `.env` files and unrelated changes out of commits.
- Push only when asked. Never force-push, `reset --hard`, or rewrite pushed history without explicit approval.
