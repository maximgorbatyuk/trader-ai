# Documentation guidance

The root `AGENTS.md` also applies here.

## Audience and ownership

- `README.md` and `docs/` are human-facing. Describe product behavior and durable architecture rather than agent workflow.
- `docs/architecture.md` owns cross-subsystem structure and rationale.
- Focused pages under `docs/logic/`, `docs/roles/`, and `docs/rules/` own detailed business behavior.
- Agent-only paths, edge cases, and implementation constraints belong in scoped `AGENTS.md` files. Never link to those files from human documentation.

## Writing rules

- Do not duplicate source code or repeat long behavior lists already owned by another document. Link to the canonical page instead.
- Avoid library versions, line numbers, directory item counts, exhaustive file lists, and other details that rot quickly.
- Refer to stable product terms and section headings. Keep implementation identifiers out unless the document genuinely requires one.
- Before writing an endpoint, route, field, helper, configuration key, or other code-level identifier, verify that it exists verbatim with `rtk rg`.
- Describe features rather than narrating how a particular commit implemented them.
- Never reference plan paragraphs, pull requests, or ticket identifiers.

## Synchronization

- Add every new documentation page to the README documentation table.
- Update architecture documentation when a subsystem boundary or durable pattern changes.
- Update the relevant role or logic page when product behavior changes.
- Keep `CLAUDE.md` files as thin redirects to their corresponding `AGENTS.md`; do not place substantive guidance in them.

## Verification

- Check relative Markdown links and referenced paths.
- Run `rtk git diff --check` before reporting completion.
