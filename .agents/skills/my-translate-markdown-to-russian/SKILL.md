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
3. If the output already exists, overwrite it.
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
- one embedded `<style>` block in `<head>` that makes the document comfortable to read;
- one complete `<body>`;
- equivalent headings, paragraphs, lists, tables, blockquotes, links, images, and code placement.

HTML-escape code where required so its rendered text remains byte-for-byte equivalent after entity decoding. Keep the document self-contained and offline: no JavaScript, no network-loaded assets (fonts, stylesheets, images, or scripts), no translator notes, generated-by notices, or extra prose sections. The inlined `<style>` block is the only decoration permitted; it must not add or alter any visible text.

### Readability stylesheet

Embed this light-theme stylesheet verbatim in `<head>`. It uses only system fonts and loads nothing over the network:

```html
<style>
  :root { color-scheme: light; }
  body {
    max-width: 46rem;
    margin: 0 auto;
    padding: 2.5rem 1.25rem 4rem;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
    font-size: 1rem;
    line-height: 1.65;
    color: #1f2328;
    background: #ffffff;
  }
  h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 2rem 0 1rem; font-weight: 600; color: #111418; }
  h1 { font-size: 2rem; padding-bottom: .3rem; border-bottom: 1px solid #d8dee4; }
  h2 { font-size: 1.5rem; padding-bottom: .3rem; border-bottom: 1px solid #d8dee4; }
  h3 { font-size: 1.25rem; }
  p { margin: 0 0 1rem; }
  a { color: #0969da; text-decoration: none; }
  a:hover { text-decoration: underline; }
  ul, ol { margin: 0 0 1rem; padding-left: 1.75rem; }
  li { margin: .25rem 0; }
  blockquote { margin: 0 0 1rem; padding: .25rem 1rem; color: #59636e; border-left: .25rem solid #d0d7de; }
  blockquote > :last-child { margin-bottom: 0; }
  code { font-family: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace; font-size: .9em; background: #eff1f3; padding: .15em .35em; border-radius: 4px; }
  pre { margin: 0 0 1rem; padding: 1rem; overflow: auto; background: #f6f8fa; border: 1px solid #d8dee4; border-radius: 6px; line-height: 1.45; }
  pre code { background: none; padding: 0; font-size: .875rem; }
  table { border-collapse: collapse; width: 100%; margin: 0 0 1rem; display: block; overflow: auto; }
  th, td { border: 1px solid #d0d7de; padding: .4rem .75rem; text-align: left; }
  th { background: #f6f8fa; font-weight: 600; }
  img { max-width: 100%; height: auto; }
  hr { border: none; border-top: 1px solid #d8dee4; margin: 2rem 0; }
</style>
```

## Example

For `/docs/payment.guide.md`, write `/docs/payment.guide.html`. Translate “Submit a payment” as prose, but keep `PaymentGateway.SubmitPaymentAsync()`, `retryCount`, `HTTP 429`, and every code-block character unchanged.

## Common mistakes

- Do not translate comments or strings inside code blocks.
- Do not transliterate identifiers.
- Do not treat the validator as proof of semantic fidelity; complete the comparison pass.
- Do not modify or delete the Markdown source.
- In the final response, report the HTML path and validation result without adding suggestions.
