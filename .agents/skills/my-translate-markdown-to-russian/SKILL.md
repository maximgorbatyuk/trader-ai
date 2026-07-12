---
name: my-translate-markdown-to-russian
description: Use when a user passes a Markdown (.md) file and requests a faithful Russian HTML translation with code and technical content preserved.
---

# Translate Markdown to Russian

## Core rule

Translate every and only natural-language prose into Russian. Preserve the source meaning exactly: do not explain, improve, correct, summarize, shorten, expand, recommend, or add content.

When unsure whether text is prose or a technical token, leave it unchanged.

## Workflow

1. Resolve the single passed file to an absolute path. Require an existing UTF-8 file whose extension is `.md`.
2. Set the output to `source.with_suffix(".html")`. This keeps the source directory and full basename, including earlier dots.
3. If the output already exists and the user did not explicitly authorize replacement, ask before overwriting it.
4. Record the source file's SHA-256 hash before writing anything.
5. Inventory protected content, translate the prose, render a standalone HTML5 document, and write only the output file.
6. Run the bundled validator from this skill directory:

```bash
python3 scripts/validate_translation.py "/absolute/path/source.md" "/absolute/path/source.html"
```

7. Recompute the source hash and require it to match. Then compare each source heading, paragraph, list item, table cell, and quotation with its rendered Russian counterpart. Correct omissions, additions, changed modality, changed negation, and changed facts, then rerun validation.

## Protected content

Preserve these exactly, including case, spelling, punctuation, and order:

- fenced and indented code blocks in full, including comments and string literals;
- inline code and raw HTML;
- class, interface, type, method, function, variable, parameter, property, namespace, and enum names, even when not marked as code;
- commands, flags, environment variables, configuration keys, package names, versions, API routes, filenames, and filesystem paths;
- URLs and Markdown link or image destinations;
- numbers, formulas, placeholders, template expressions, units, dates, and currency values.

Translate link labels and image alt text only when they are ordinary prose. Preserve YAML or TOML frontmatter as technical metadata and do not render it as visible prose.

## HTML requirements

Produce semantic HTML matching the Markdown hierarchy and order. Require:

- `<!doctype html>`;
- `<html lang="ru">`;
- UTF-8 charset metadata and a non-empty translated `<title>`;
- one complete `<body>`;
- equivalent headings, paragraphs, lists, tables, blockquotes, links, images, and code placement.

HTML-escape code where required so its rendered text remains byte-for-byte equivalent after entity decoding. Add no translator notes, generated-by notices, extra sections, JavaScript, external assets, or decorative content.

## Example

For `/docs/payment.guide.md`, write `/docs/payment.guide.html`. Translate “Submit a payment” as prose, but keep `PaymentGateway.SubmitPaymentAsync()`, `retryCount`, `HTTP 429`, and every code-block character unchanged.

## Common mistakes

- Do not translate comments or strings inside code blocks.
- Do not transliterate identifiers.
- Do not treat the validator as proof of semantic fidelity; complete the comparison pass.
- Do not modify or delete the Markdown source.
- In the final response, report the HTML path and validation result without adding suggestions.
