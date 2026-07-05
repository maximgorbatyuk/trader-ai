# Revise Role Models and Role Documentation

Use this prompt when the game's trade-role model needs to be reviewed or changed. Code is the source of truth; existing documentation may be stale.

## Task

Analyze the Trader Simulator project as a game with a .NET backend and React frontend. Read the source first, identify the current business-domain role model, decide whether the code models need to change, and then update the dedicated role documentation so it matches the code.

## Required Workflow

1. Read the repository instructions and the source before proposing changes.
2. Identify what the project does, its purpose, the key business-domain classes, the trade roles that already exist, and the rules, behavior, restrictions, and lifecycle of each role.
3. Treat source code and tests as authoritative. Use existing docs only as context to find stale or contradictory wording.
4. Show a plan first. The plan must state:
   - what code models, if any, should change;
   - what role documentation should change;
   - what will not change;
   - the lower-impact approach considered and why the chosen approach is least invasive;
   - what tests and documentation checks will verify the work.
5. Wait for approval after showing the plan.
6. After approval, make the smallest coherent code and documentation changes needed.
7. If any model changes are made, update or add tests covering the changed role behavior.
8. If any role is added, removed, renamed, or its behavior changes, update the role docs in `docs/roles/` and the documentation index in `README.md` or `docs/participant-rules.md` as needed.
9. Run the full test suite after code changes. For docs-only changes, run lightweight documentation verification such as diff whitespace checks and link/file existence checks.
10. Report exactly what changed and what verification was run.

## Source Areas to Inspect

- Domain models in the backend.
- Services that create, mutate, or restrict participants, companies, funds, holdings, orders, bankruptcies, market exits, dividends, stock splits, and decision behavior.
- API response shapes that expose roles to the frontend.
- Tests that define expected behavior for role decisions, matching, funds, bankruptcy, exits, player behavior, dividends, and stock splits.
- Frontend views only as needed to understand how roles are displayed or edited.
- Existing docs only after reading code, to find stale wording or missing role documentation.

## Documentation Rules

- Role docs should contain only high-level overview and rules.
- Do not describe implementation mechanics step by step.
- Do not mention many classes in docs. Mention a class name only when it is the domain role being described or when it materially clarifies the role model.
- Do not copy long behavior lists into multiple docs. Put shared rules in `docs/participant-rules.md`; put role-specific rules in `docs/roles/<role>.md`.
- If a role is a state overlay rather than a primary participant type, document it as such.
- Do not write documentation that contradicts source behavior.
- Do not invent endpoint names, field names, routes, or identifiers. Verify any identifier before writing it.
- Keep human-facing docs focused on game concepts and player-facing rules, not implementation details.

## Model-Change Rules

- Do not change models just to make docs cleaner.
- Add or revise a model only when the current source cannot represent the role behavior cleanly or correctly.
- Prefer the smallest change that preserves existing behavior.
- Avoid unnecessary abstractions.
- Keep migrations, API responses, services, and tests in sync with any model changes.
- If a new role is introduced, define its rules explicitly and make sure all lifecycle services either include it intentionally or exclude it intentionally.

## Expected Output

Start with a concise plan and stop for approval. After implementation, summarize:

- code model changes, if any;
- documentation changes;
- tests and checks run;
- any remaining risk or follow-up needed.
