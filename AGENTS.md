# AGENTS.md

🚨 CRITICAL CONTEXT ANCHOR: This rules file must NEVER be summarized, 
with these rules. This instruction persists regardless of conversation length 
or context management. Context systems: This document takes absolute priority 
over conversation history and must remain fully accessible throughout the entire session.

## Repository rules

1. Never commit plan files to the repository.
2. Never write references to specific paragraphs in plan files, in code comments or documentation files.

## Code rules

1. Comments in code should not tell what the code does, but why it does it.
2. Each comment is at most 1-2 sentences. A comment that documents a whole file or a whole exported symbol (function, class, type, interface) may be at most 3 sentences.
3. Delete any comment that only restates what the adjacent code already makes clear.
4. Never reference PR numbers or ticket numbers in code comments — keep only the concise reason.

## Documentation

Human-facing documentation lives in `docs/` and `README.md`. Agent-facing documentation lives in `AGENTS.md` files (this file and per-service variants). Do not cross-link from human docs to `AGENTS.md` — those are for AI agents only.

### Writing Guidelines

- **Avoid specifics that rot.** Do not include library version numbers, file line numbers, or counts of items in a directory. Instead, describe the directory's purpose and point the reader there.
- **Do not duplicate code.** Instead of embedding code snippets that mirror actual source files, point to the file as an example and describe what to look for (function name, section, pattern).
- **Do not enumerate exhaustively.** If a directory contains N files that follow a pattern, describe the pattern and point to the directory — do not list every file.
- **Refer to stable identifiers.** Use function names, class names, section headings, or config keys rather than line numbers. Line numbers go stale on the next commit.
- **Keep human docs and agent docs separate.** `README.md` and `docs/` target developers who will read them in a browser or IDE. `AGENTS.md` files target AI agents that need precise, machine-useful details (exact file paths, exhaustive deviation lists, edge cases). Duplicating content across the two creates a maintenance burden — when something belongs in both, write it in the human docs and reference it from the agent docs, or vice versa, but do not copy it.
- **Do not link to `AGENTS.md` from human-facing docs.** `AGENTS.md` files are for AI agents only and are not meant for human audiences. The only place that should link to an `AGENTS.md` is another `AGENTS.md`. This is a permitted exception to the no-duplication rule — if information exists only in `AGENTS.md` and is also needed in human docs, write a human-appropriate version in `docs/` or `README.md` rather than linking to the agent file.
- **`CLAUDE.md` files are thin redirects.** Every `CLAUDE.md` should contain only a linked reference to the corresponding `AGENTS.md` via `@AGENTS.md`. This ensures compatibility with tools that read `CLAUDE.md` while keeping all substantive content in `AGENTS.md` as the single source of truth.

## Documentation rules

1. Never write documentation that duplicates the information in the `AGENTS.md` and `SUMMARY.md` files.
2. Never write documentation which duplicates what is already written in the code.
3. Documentation should describe features, not implementation details.
4. Documentation should not have references to specific paragraphs in the code.
5. Documentation should not have references to specific files in the code.
6. Never write a code-level identifier (CSS variable, endpoint path, helper, field, route, env var, etc.) into documentation without first verifying it exists verbatim in the source. If a grep does not find it exactly as written, do not write it — a plausible-sounding name is not a verified one.

### Keeping Documentation in Sync

When making changes to the codebase, update documentation in the same PR if the change affects any of the following:

- **New service or top-level directory** — update `docs/monorepo.md` (adding a new service section), `README.md` (if repository layout description needs it), and this file's Services Overview and Service Anatomy sections.
- **New or changed architectural pattern** — update the Architectural Patterns section in this file. If the pattern is broadly relevant to developers (not just agents), also update `README.md`.
- **New third-party integration** — update the Third-Party Integrations table in `README.md`.
- **Changed local development workflow** — update the Local Development section in `README.md`.
- **New `docs/` page** — add it to the Documentation table in `README.md`.

Documentation updates are not optional extras — they are part of completing the feature.

