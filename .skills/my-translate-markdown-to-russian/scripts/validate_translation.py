#!/usr/bin/env python3

import argparse
import re
import sys
from collections import Counter
from html import unescape
from pathlib import Path


FENCE_OPEN_PATTERN = re.compile(r"^[ \t]{0,3}(`{3,}|~{3,})")
FENCE_CLOSE_PATTERN = re.compile(r"^[ \t]{0,3}(`+|~+)[ \t]*(?:\r?\n)?$")
INLINE_CODE_PATTERN = re.compile(
    r"(?<!`)(?P<ticks>`+)(?!`)(?P<code>.*?)(?<!`)(?P=ticks)(?!`)",
    re.DOTALL,
)
URL_PATTERN = re.compile(r"https?://[^\s<>()\[\]\"']+")
NUMBER_PATTERN = re.compile(r"(?<!\w)\d+(?:\.\d+)*(?!\w)")
MARKDOWN_DESTINATION_PATTERN = re.compile(
    r"!?\[[^\]\n]*\]\(\s*<?([^)\s>]+)>?"
)
RAW_HTML_PATTERN = re.compile(r"</?[A-Za-z][^>\n]*>")
TECHNICAL_PATTERNS = (
    re.compile(r"\b(?:[A-Za-z_]\w*\.)+[A-Za-z_]\w*(?:\(\))?"),
    re.compile(r"\b[A-Z][a-z0-9]+(?:[A-Z][A-Za-z0-9]*)+\b"),
    re.compile(r"\b[a-z]+(?:[A-Z][A-Za-z0-9]*)+\b"),
    re.compile(r"\b[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+\b"),
    re.compile(r"\b[A-Za-z_]\w*\(\)"),
    re.compile(r"(?<!\w)--[A-Za-z0-9][A-Za-z0-9_-]*"),
    re.compile(r"(?<![\w:])(?:/|\.{1,2}/)[A-Za-z0-9._~!$&'+,;=:@%/-]+"),
    re.compile(
        r"(?<!\w)\d+(?:[.,]\d+)?"
        r"(?:ns|ms|s|min|h|B|KB|MB|GB|TB|KiB|MiB|GiB|Hz|kHz|MHz|GHz|px|rem|em|%)"
        r"(?!\w)"
    ),
    re.compile(
        r"(?:[$€£¥₽]\s?\d+(?:[.,]\d+)*|"
        r"\d+(?:[.,]\d+)*\s?(?:USD|EUR|GBP|JPY|RUB))"
    ),
    re.compile(r"(?:\{\{.*?\}\}|\$\{[^}\n]+\}|<%.*?%>|%[A-Za-z_]\w*%)"),
    re.compile(
        r"\b(?:API|ASCII|CPU|CSS|CSV|DNS|DOM|HTML|HTTP|HTTPS|ID|IP|JSON|"
        r"RAM|REST|SDK|SQL|SSH|TCP|TLS|UI|URI|URL|UTF-?8|UUID|XML)\b"
    ),
)


def strip_frontmatter(markdown):
    lines = markdown.splitlines(keepends=True)
    if not lines or lines[0].strip() not in {"---", "+++"}:
        return markdown

    delimiter = lines[0].strip()
    for index, line in enumerate(lines[1:], start=1):
        if line.strip() == delimiter:
            return "".join("\n" for _ in lines[: index + 1]) + "".join(
                lines[index + 1 :]
            )
    return markdown


def split_fenced_regions(markdown):
    code_blocks = []
    outside_lines = []
    active_character = None
    minimum_closing_length = 0
    current_code = []

    for line in markdown.splitlines(keepends=True):
        if active_character is None:
            opening = FENCE_OPEN_PATTERN.match(line)
            if not opening:
                outside_lines.append(line)
                continue

            fence = opening.group(1)
            active_character = fence[0]
            minimum_closing_length = len(fence)
            current_code = []
            outside_lines.append("\n" if line.endswith("\n") else "")
            continue

        closing = FENCE_CLOSE_PATTERN.match(line)
        if (
            closing
            and closing.group(1)[0] == active_character
            and len(closing.group(1)) >= minimum_closing_length
        ):
            code_blocks.append("".join(current_code))
            active_character = None
            minimum_closing_length = 0
            current_code = []
        else:
            current_code.append(line)
        outside_lines.append("\n" if line.endswith("\n") else "")

    if active_character is not None:
        code_blocks.append("".join(current_code))

    return code_blocks, "".join(outside_lines)


def extract_fenced_code(markdown):
    return split_fenced_regions(strip_frontmatter(markdown))[0]


def extract_indented_code(markdown):
    markdown_without_fences = split_fenced_regions(strip_frontmatter(markdown))[1]
    blocks = []
    current = []
    pending_blank_lines = []
    can_start_block = True
    inside_list = False

    for line in markdown_without_fences.splitlines(keepends=True):
        is_indented = line.startswith("    ") or line.startswith("\t")
        is_blank = not line.strip()

        if current and is_indented:
            current.extend(pending_blank_lines)
            pending_blank_lines.clear()
            current.append(line[4:] if line.startswith("    ") else line[1:])
            continue
        if current and is_blank:
            pending_blank_lines.append(line)
            continue
        if current:
            blocks.append("".join(current))
            current = []
            pending_blank_lines = []

        if is_blank:
            can_start_block = True
            continue
        if re.match(r"^[ \t]{0,3}(?:[-+*]|\d+[.)])\s+", line):
            inside_list = True
            can_start_block = False
            continue
        if is_indented:
            if can_start_block and not inside_list:
                current.append(line[4:] if line.startswith("    ") else line[1:])
            can_start_block = False
            continue

        if not line.startswith((" ", "\t")):
            inside_list = False
        can_start_block = False

    if current:
        blocks.append("".join(current))

    return blocks


def extract_protected_fragments(markdown):
    markdown_without_frontmatter = strip_frontmatter(markdown)
    _, markdown_without_fences = split_fenced_regions(markdown_without_frontmatter)
    fragments = {
        match.group("code") for match in INLINE_CODE_PATTERN.finditer(markdown_without_fences)
    }
    fragments.update(
        match.rstrip(".,;:!?")
        for match in URL_PATTERN.findall(markdown_without_fences)
    )
    fragments.update(NUMBER_PATTERN.findall(markdown_without_fences))
    fragments.update(MARKDOWN_DESTINATION_PATTERN.findall(markdown_without_fences))
    fragments.update(RAW_HTML_PATTERN.findall(markdown_without_fences))
    for pattern in TECHNICAL_PATTERNS:
        fragments.update(pattern.findall(markdown_without_fences))
    return Counter(
        {
            fragment: markdown_without_fences.count(fragment)
            for fragment in fragments
            if fragment
        }
    )


def contains_code(decoded_html, code):
    return code.rstrip("\r\n") in decoded_html


def extract_decoded_body(html):
    body = re.search(
        r"<body\b[^>]*>(?P<content>.*?)</body\s*>",
        html,
        re.IGNORECASE | re.DOTALL,
    )
    return unescape(body.group("content") if body else html)


def validate_document_shell(html):
    errors = []
    if not re.search(r"<!doctype\s+html\b", html, re.IGNORECASE):
        errors.append("Missing <!doctype html> declaration.")
    if not re.search(
        r"<html\b[^>]*\blang\s*=\s*([\"'])ru\1",
        html,
        re.IGNORECASE,
    ):
        errors.append('Missing lang="ru" on the <html> element.')
    if not re.search(
        r"<meta\b[^>]*\bcharset\s*=\s*([\"'])?utf-8\1?[^>]*>",
        html,
        re.IGNORECASE,
    ):
        errors.append("Missing UTF-8 charset metadata.")
    if not re.search(r"<title\b[^>]*>\s*[^<\s]", html, re.IGNORECASE):
        errors.append("Missing a non-empty <title> element.")
    if not re.search(r"<body\b[^>]*>.*?</body\s*>", html, re.IGNORECASE | re.DOTALL):
        errors.append("Missing a complete <body> element.")
    return errors


def validate(source_path, output_path):
    errors = []

    if source_path.suffix.lower() != ".md":
        errors.append(f"Source file must use the .md extension: {source_path}")
    if not source_path.is_file():
        errors.append(f"Source Markdown file does not exist: {source_path}")

    expected_output = source_path.with_suffix(".html")
    if output_path.resolve() != expected_output.resolve():
        errors.append(f"Output path must be {expected_output}")
    if not output_path.is_file():
        errors.append(f"Output HTML file does not exist: {output_path}")

    if errors:
        return errors

    try:
        markdown = source_path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        return [f"Source Markdown is not valid UTF-8: {source_path}"]

    try:
        html = output_path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        return [f"Output HTML is not valid UTF-8: {output_path}"]

    errors.extend(validate_document_shell(html))
    decoded_html = extract_decoded_body(html)

    for index, code in enumerate(extract_fenced_code(markdown), start=1):
        if not contains_code(decoded_html, code):
            errors.append(f"Changed or missing fenced code block {index}.")

    for index, code in enumerate(extract_indented_code(markdown), start=1):
        if not contains_code(decoded_html, code):
            errors.append(f"Changed or missing indented code block {index}.")

    protected_fragments = extract_protected_fragments(markdown)
    for fragment, required_count in sorted(protected_fragments.items()):
        actual_count = decoded_html.count(fragment)
        if actual_count < required_count:
            errors.append(
                "Changed or missing protected content "
                f"{fragment!r}: expected at least {required_count}, found {actual_count}."
            )

    return errors


def parse_args():
    parser = argparse.ArgumentParser(
        description="Validate a Russian HTML translation against its Markdown source."
    )
    parser.add_argument("source", type=Path, help="Source Markdown file")
    parser.add_argument("output", type=Path, help="Translated HTML file")
    return parser.parse_args()


def main():
    args = parse_args()
    errors = validate(args.source, args.output)
    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    print(
        "Guardrail validation passed; semantic review is still required: "
        f"{args.output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
