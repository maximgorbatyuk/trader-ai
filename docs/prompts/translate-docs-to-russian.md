# Translate Markdown Documentation to Russian

Use this prompt to translate the repository's English Markdown documentation into Russian in place. The translation must contain exactly the source meaning and no additional content.

## Task

Translate every eligible English Markdown file under the repository-relative `docs/` directory into Russian, then replace that same file's contents with the translation.

Do not create HTML, translated copies, backup files, or other output files. This task intentionally updates the eligible Markdown sources in place, even if an available translation workflow normally produces another format.

## Scope

Discover Markdown files recursively under `docs/`, including files directly inside `docs/` and files in its other subdirectories.

Exclude all of the following:

- every file under `docs/plans/`;
- every file under `docs/prompts/`;
- every file whose basename is `AGENTS.md`, `README.md`, or `CLAUDE.md`, wherever it appears;
- non-regular files and files outside `docs/`.

Use repository-relative paths. A suitable initial discovery command is:

```zsh
rtk rg --files docs \
  -g '*.md' \
  -g '!docs/plans/**' \
  -g '!docs/prompts/**' \
  -g '!**/AGENTS.md' \
  -g '!**/README.md' \
  -g '!**/CLAUDE.md'
```

Confirm that the protected paths and basenames are absent from that result before reading or writing any candidate.

## Translation Rule

Translate every and only natural-language English prose into Russian. Preserve the source meaning exactly. Do not explain, improve, correct, summarize, shorten, expand, modernize, recommend, or add content.

The translation must preserve every assertion, qualification, condition, exception, negation, modality, fact, and relationship from the source. Do not add translator notes, generated-by notices, headings, examples, warnings, conclusions, or any other sentences that are absent from the source.

Translate natural-language prose in:

- headings;
- paragraphs;
- list items;
- table cells;
- blockquotes;
- link labels and image alt text when they are ordinary prose;
- footnote text;
- visible text nodes inside safe presentational HTML, while leaving the HTML markup unchanged.

Preserve existing Russian prose exactly. For a mixed-language document, translate only its remaining English prose. Do not rephrase or normalize the Russian portions.

When unsure whether a token is ordinary prose or technical content, leave it unchanged.

## Russian-Language Files

Classify each candidate by its natural-language prose, ignoring code and other protected technical content.

- If the document's natural-language prose is already Russian and it contains no material English prose requiring translation, skip the entire file without writing it.
- Do not classify a document as English merely because code, identifiers, product names, commands, or other protected tokens use English words.
- Do not classify a mixed-language document as already Russian when it still contains material English prose. Translate only that English prose.
- If the language cannot be determined confidently, leave the file unchanged and report it as skipped due to uncertain language.

## Protected Content

Preserve the following exactly, including case, spelling, punctuation, whitespace, and order where applicable:

- fenced and indented code blocks in full, including comments, string literals, and fence language identifiers;
- inline code;
- scripts and command examples;
- HTML comments and the contents of `script`, `style`, `code`, and `pre` elements;
- YAML, TOML, or other frontmatter;
- class, interface, type, method, function, variable, parameter, property, namespace, module, and enum names, even when they are not marked as code;
- commands, subcommands, flags, environment variables, configuration keys, package names, library names, product names, and versions;
- API routes, endpoint names, HTTP methods, status codes, field names, and serialized values;
- filenames, filesystem paths, URLs, email addresses, Markdown link and image destinations, and reference identifiers;
- numbers, formulas, placeholders, template expressions, units, dates, times, and currency values;
- raw HTML tags and attributes.

Do not translate, transliterate, rename, reformat, or repair protected content.

## Structure Preservation

Keep the original Markdown structure and content order. Preserve:

- heading levels;
- paragraph boundaries;
- ordered and unordered list types, nesting, and numbering;
- task-list markers;
- table columns, alignment markers, and row order;
- blockquote depth;
- links, images, footnotes, and reference-definition relationships;
- thematic breaks;
- code placement;
- frontmatter position;
- blank-line separation and whitespace-sensitive regions.

Do not reorder sections, lists, sentences, clauses, examples, or table rows. Do not merge or split sections, paragraphs, list items, table cells, sentences, or other content units.

## Required Workflow

1. Read the repository instructions that apply to `docs/` before making changes.
2. Record the initial working-tree status and preserve all unrelated changes.
3. Discover the candidate files and apply every exclusion before opening files for translation.
4. Inspect each candidate's natural-language prose and classify it as English, Russian, mixed, or uncertain.
5. Skip Russian and uncertain files according to the language rules above.
6. For each English or mixed file, inventory its protected content and Markdown structure before translating.
7. Translate the document prose from beginning to end, preserving a one-to-one correspondence with the source content.
8. Compare the completed translation with the original before writing. Check for omissions, additions, changed facts, changed negation, changed modality, renamed technical tokens, and structural drift.
9. Replace the original file only after the complete translation has passed that comparison. Do not leave temporary files behind.
10. Re-read every changed file and verify it against the original inventory.
11. Inspect the complete documentation diff and correct every fidelity or formatting problem before finishing.

Do not use destructive Git commands. Do not discard, overwrite, or reformat unrelated user changes.

## Verification

Before reporting completion, verify all of the following:

- only eligible Markdown files were changed;
- no file under `docs/plans/` or `docs/prompts/` changed;
- no `AGENTS.md`, `README.md`, or `CLAUDE.md` changed;
- every file classified as already Russian remained byte-for-byte unchanged;
- all protected content in each translated file matches its original content;
- Markdown hierarchy, ordering, link destinations, and image destinations are unchanged;
- every source prose unit has one translated counterpart and no translated prose unit lacks a source counterpart;
- no translated file contains translator commentary or newly invented content;
- no temporary, backup, HTML, or parallel translation file was created.

Run at least:

```zsh
rtk git diff --check
rtk git status --short
rtk git diff -- docs
```

Review the actual diff rather than treating successful commands as proof of semantic fidelity. This is a documentation-only task, so do not run application test suites unless another repository instruction specifically requires them for documentation changes.

## Completion Report

Report only:

- the files translated in place;
- the files skipped because they were already Russian;
- any files skipped because their language was uncertain;
- the verification commands run and their results.

Do not commit unless explicitly asked.
