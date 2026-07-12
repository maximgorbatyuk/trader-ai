import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "validate_translation.py"

SOURCE_MARKDOWN = """# Payment retry guide

Use `PaymentGateway.SubmitPaymentAsync()` to submit a payment. Retry HTTP 429 responses up to 3 times.

Do not rename the PaymentGateway class or the retryCount parameter.

```csharp
public async Task<PaymentResult> SubmitPaymentAsync(
    PaymentRequest request,
    int retryCount)
{
    // Return the provider response unchanged.
    return await client.PostAsync("/api/v1/payments", request);
}
```

See [the API reference](https://example.com/docs/payments?version=1.2).
"""

VALID_HTML = """<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <title>Руководство по повторным попыткам оплаты</title>
  <style>
    body { max-width: 46rem; margin: 0 auto; padding: 2.5rem 1.25rem; line-height: 1.65; color: #1f2328; }
    pre { background: #f6f8fa; padding: 1rem; overflow: auto; }
  </style>
</head>
<body>
  <h1>Руководство по повторным попыткам оплаты</h1>
  <p>Используйте <code>PaymentGateway.SubmitPaymentAsync()</code>, чтобы отправить платёж. Повторяйте ответы HTTP 429 до 3 раз.</p>
  <p>Не переименовывайте класс PaymentGateway или параметр retryCount.</p>
  <pre><code class="language-csharp">public async Task&lt;PaymentResult&gt; SubmitPaymentAsync(
    PaymentRequest request,
    int retryCount)
{
    // Return the provider response unchanged.
    return await client.PostAsync("/api/v1/payments", request);
}
</code></pre>
  <p>См. <a href="https://example.com/docs/payments?version=1.2">справочник API</a>.</p>
</body>
</html>
"""


def html_document(body, title="Пример"):
    return f"""<!doctype html>
<html lang="ru">
<head><meta charset="utf-8"><title>{title}</title><style>body{{max-width:46rem;margin:0 auto}}</style></head>
<body>{body}</body>
</html>
"""


class ValidatorCliTests(unittest.TestCase):
    def setUp(self):
        temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(temporary_directory.cleanup)
        self.directory = Path(temporary_directory.name)
        self.source = self.directory / "guide.en.md"
        self.output = self.directory / "guide.en.html"
        self.source.write_text(SOURCE_MARKDOWN, encoding="utf-8")
        self.output.write_text(VALID_HTML, encoding="utf-8")

    def run_validator(self, output=None):
        self.assertTrue(
            SCRIPT.is_file(),
            "validator script has not been implemented",
        )
        return subprocess.run(
            [
                sys.executable,
                str(SCRIPT),
                str(self.source),
                str(output or self.output),
            ],
            capture_output=True,
            text=True,
            check=False,
        )

    def test_accepts_a_complete_faithful_translation(self):
        result = self.run_validator()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("Guardrail validation passed", result.stdout)
        self.assertIn("semantic review is still required", result.stdout)

    def test_accepts_a_code_block_without_the_fence_separator_newline(self):
        html_without_separator_newline = VALID_HTML.replace(
            "}\n</code></pre>",
            "}</code></pre>",
        )
        self.output.write_text(html_without_separator_newline, encoding="utf-8")

        result = self.run_validator()

        self.assertEqual(result.returncode, 0, result.stderr)

    def test_rejects_an_output_path_with_a_different_basename(self):
        wrong_output = self.directory / "translated.html"
        wrong_output.write_text(VALID_HTML, encoding="utf-8")

        result = self.run_validator(wrong_output)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("guide.en.html", result.stderr)

    def test_rejects_an_html_fragment_without_document_metadata(self):
        self.output.write_text("<h1>Перевод</h1>", encoding="utf-8")

        result = self.run_validator()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("doctype", result.stderr.lower())
        self.assertIn('lang="ru"', result.stderr)
        self.assertIn("UTF-8", result.stderr)

    def test_rejects_a_document_without_a_stylesheet(self):
        without_style = re.sub(
            r"<style\b[^>]*>.*?</style>",
            "",
            VALID_HTML,
            flags=re.IGNORECASE | re.DOTALL,
        )
        self.output.write_text(without_style, encoding="utf-8")

        result = self.run_validator()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("style", result.stderr.lower())

    def test_rejects_a_changed_fenced_code_block(self):
        changed = VALID_HTML.replace(
            "// Return the provider response unchanged.",
            "// Вернуть ответ провайдера без изменений.",
        )
        self.output.write_text(changed, encoding="utf-8")

        result = self.run_validator()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("fenced code block", result.stderr)

    def test_rejects_a_changed_indented_code_block(self):
        self.source.write_text(
            "# Example\n\n    print('hello world')\n",
            encoding="utf-8",
        )
        self.output.write_text(
            """<!doctype html>
<html lang="ru">
<head><meta charset="utf-8"><title>Пример</title></head>
<body><h1>Пример</h1><pre><code>print('привет мир')
</code></pre></body>
</html>
""",
            encoding="utf-8",
        )

        result = self.run_validator()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("indented code block", result.stderr)

    def test_rejects_changed_code_with_a_longer_closing_fence(self):
        self.source.write_text(
            """# Example

````python
print("hello world")
`````
""",
            encoding="utf-8",
        )
        self.output.write_text(
            html_document(
                '<h1>Пример</h1><pre><code>print("привет мир")</code></pre>'
            ),
            encoding="utf-8",
        )

        result = self.run_validator()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("fenced code block", result.stderr)

    def test_rejects_changed_multi_backtick_inline_code(self):
        self.source.write_text(
            "# Example\n\nUse ``render(`value`)`` now.\n",
            encoding="utf-8",
        )
        self.output.write_text(
            html_document(
                "<h1>Пример</h1><p>Используйте <code>рендер(`значение`)</code>.</p>"
            ),
            encoding="utf-8",
        )

        result = self.run_validator()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("render(`value`)", result.stderr)

    def test_rejects_changed_unformatted_technical_tokens(self):
        self.source.write_text(
            "# Example\n\n"
            "Run render() --force against /api/v1/payments in 10ms for $5 "
            "and open [config](docs/app.md#retry).\n",
            encoding="utf-8",
        )
        self.output.write_text(
            html_document(
                "<h1>Пример</h1><p>Запустите рендер с принудительным режимом "
                "для конечной точки платежей за 10 мс стоимостью 5 долларов "
                "и откройте конфигурацию.</p>"
            ),
            encoding="utf-8",
        )

        result = self.run_validator()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("render()", result.stderr)
        self.assertIn("--force", result.stderr)
        self.assertIn("/api/v1/payments", result.stderr)
        self.assertIn("10ms", result.stderr)
        self.assertIn("$5", result.stderr)
        self.assertIn("docs/app.md#retry", result.stderr)

    def test_accepts_translated_list_continuation_prose(self):
        self.source.write_text(
            "# Steps\n\n- First item\n    This continuation should be translated.\n",
            encoding="utf-8",
        )
        self.output.write_text(
            html_document(
                "<h1>Шаги</h1><ul><li>Первый пункт. "
                "Это продолжение должно быть переведено.</li></ul>",
                title="Шаги",
            ),
            encoding="utf-8",
        )

        result = self.run_validator()

        self.assertEqual(result.returncode, 0, result.stderr)

    def test_accepts_a_translated_uppercase_prose_heading(self):
        self.source.write_text(
            "# WARNING\n\nProceed carefully.\n",
            encoding="utf-8",
        )
        self.output.write_text(
            html_document(
                "<h1>ПРЕДУПРЕЖДЕНИЕ</h1><p>Действуйте осторожно.</p>",
                title="ПРЕДУПРЕЖДЕНИЕ",
            ),
            encoding="utf-8",
        )

        result = self.run_validator()

        self.assertEqual(result.returncode, 0, result.stderr)

    def test_head_content_cannot_mask_a_missing_body_identifier(self):
        self.source.write_text(
            "# PaymentGateway\n\nUse PaymentGateway.\n",
            encoding="utf-8",
        )
        self.output.write_text(
            html_document(
                "<h1>Платёжный шлюз</h1><p>Используйте PaymentGateway.</p>",
                title="PaymentGateway",
            ),
            encoding="utf-8",
        )

        result = self.run_validator()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("PaymentGateway", result.stderr)

    def test_rejects_a_changed_plain_technical_identifier(self):
        changed = VALID_HTML.replace(
            "класс PaymentGateway",
            "класс ПлатежныйШлюз",
        )
        self.output.write_text(changed, encoding="utf-8")

        result = self.run_validator()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("PaymentGateway", result.stderr)

    def test_rejects_a_changed_url(self):
        changed = VALID_HTML.replace(
            "https://example.com/docs/payments?version=1.2",
            "https://example.com/docs/payments",
        )
        self.output.write_text(changed, encoding="utf-8")

        result = self.run_validator()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("https://example.com/docs/payments?version=1.2", result.stderr)

    def test_rejects_a_changed_number(self):
        changed = VALID_HTML.replace("до 3 раз", "до трёх раз")
        self.output.write_text(changed, encoding="utf-8")

        result = self.run_validator()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("3", result.stderr)


if __name__ == "__main__":
    unittest.main()
