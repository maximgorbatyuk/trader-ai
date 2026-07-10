# AGENTS.md

## Repository rules

1. Start every response with my name, "Maxim".
2. Never commit plan files to the repository.
3. Never reference specific plan paragraphs in plan files, code comments, or documentation.

## Code rules

1. Comments explain why a decision exists, not what adjacent code already says.
2. Keep comments to one or two sentences. A file-level or exported-symbol comment may use at most three sentences.
3. Delete comments that only restate the code.
4. Never reference pull requests or ticket identifiers in code comments.
5. Keep changes simple and avoid abstractions that are not required by the task.
6. After changing code covered by existing tests, run the full relevant test suite before reporting completion.

## Project guidance

Guidance is cumulative: this file applies everywhere, and a nested `AGENTS.md` adds rules for its directory tree. Read only the guidance relevant to the files being changed.

- Read `docs/architecture.md` when a change crosses subsystem boundaries or alters an architectural pattern.
- Read `frontend/AGENTS.md` and `PRODUCT.md` before changing frontend behavior or UI.
- Read `TraderAi/TraderAi/AGENTS.md` for backend-wide domain and persistence invariants.
- Read `TraderAi/TraderAi/Services/AGENTS.md` for market-cycle or service changes.
- Read `TraderAi/TraderAi/Api/AGENTS.md` for endpoint or response-contract changes.
- Read `TraderAi/TraderAi.Tests/AGENTS.md` when adding or modifying backend tests.
- Read `docs/AGENTS.md` before changing human-facing documentation.

## Documentation

Human-facing documentation lives in `README.md` and `docs/`; agent-only implementation guidance lives in `AGENTS.md` files. Keep them complementary rather than duplicating the same content.

Update documentation in the same change when code introduces or changes an architectural pattern, top-level service or directory, third-party integration, local workflow, or documentation page. A new page under `docs/` must be added to the README documentation index.

Do not link to `AGENTS.md` from human-facing documentation. Existing `CLAUDE.md` files remain thin redirects to their corresponding `AGENTS.md`.

## Design context

Frontend design and brand direction live in `PRODUCT.md`. The product is a light trading-terminal dashboard, not a marketing surface. Preserve WCAG 2.1 AA behavior, visible keyboard focus, reduced-motion support, and non-color-only market signals; reuse the existing design tokens.

## Local workflow

- Use `rtk` for shell, launch, and verification commands.
- Run `rtk ./start-dev.sh` from the repository root to start the backend and frontend together.
- Keep plan files out of commits.
