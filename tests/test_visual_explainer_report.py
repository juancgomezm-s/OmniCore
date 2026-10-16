"""Offline checks for the optional stdlib report formatter; not a Python dependency of OmniCore."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

PACKAGE = Path(__file__).resolve().parents[1] / "skills" / "visual-explainer"
spec = importlib.util.spec_from_file_location("visual_report", PACKAGE / "scripts" / "render_report.py")
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)


class VisualReportTests(unittest.TestCase):
    def setUp(self):
        self.data = json.loads((PACKAGE / "assets" / "example.json").read_text(encoding="utf-8"))

    def test_native_flow_preserves_long_unicode_labels_and_fits_requested_cells(self):
        labels = ["Contexto y conversación conservados " * 4, "界面 e\u0301 contexto", "Modelo / respuesta"]
        self.data["sections"][0]["flow"] = labels
        for width in (24, 40, 64):
            rendered = report.tui(report.validate(self.data), width)
            graph = rendered.split("```text\n", 1)[1].split("```", 1)[0]
            self.assertTrue(all(report.cells(line) <= width for line in graph.splitlines()))
            for word in " ".join(labels).split():
                self.assertIn(word, graph)
            self.assertIn("[?]", rendered)
            self.assertNotIn("\x1b", rendered)

    def test_html_escapes_input_and_has_only_local_assets_and_real_navigation(self):
        self.data["title"] = '<script>alert("source")</script>'
        self.data["sections"][0]["flow"] = ['<img src="https://remote.invalid/a" onerror="alert(1)">']
        page = report.webpage(report.validate(self.data))
        self.assertIn("&lt;script&gt;", page)
        self.assertNotIn('<img src="', page)
        self.assertNotIn('<script>alert', page)
        self.assertNotIn("<script src=", page)
        self.assertNotIn("@import", page)
        self.assertIn("default-src 'none'", page)
        self.assertIn('href="#section-1"', page)
        self.assertIn('id="section-1"', page)
        self.assertIn("window.print()", page)

    def test_bad_data_never_disappears_silently(self):
        variants = []
        bad = copy.deepcopy(self.data); bad["sections"][0]["edges"] = ["unsupported graph"]; variants.append(bad)
        bad = copy.deepcopy(self.data); bad["sections"][1]["rows"][0]["status"] = "guessed"; variants.append(bad)
        bad = copy.deepcopy(self.data); bad["summary"] += "\x1b[31m"; variants.append(bad)
        bad = copy.deepcopy(self.data); bad["sections"] = []; variants.append(bad)
        for data in variants:
            with self.assertRaises(ValueError):
                report.validate(data)

    def test_cli_creates_utf8_html_inside_workspace_without_overwriting_or_opening(self):
        with tempfile.TemporaryDirectory(prefix="omni-visual-") as directory:
            root = Path(directory)
            source = root / "input.json"
            source.write_text(json.dumps(self.data), encoding="utf-8")
            argv = [sys.executable, str(PACKAGE / "scripts" / "render_report.py"), str(source),
                    "--format", "html", "--out", "reports/ruta con espacios.html"]
            first = subprocess.run(argv, cwd=root, capture_output=True, text=True, encoding="utf-8")
            self.assertEqual(0, first.returncode, first.stderr)
            output = root / "reports" / "ruta con espacios.html"
            original = output.read_bytes()
            self.assertIn(output.as_uri(), first.stdout)
            second = subprocess.run(argv, cwd=root, capture_output=True, text=True, encoding="utf-8")
            self.assertEqual(2, second.returncode)
            self.assertEqual(original, output.read_bytes())
            outside = subprocess.run(argv[:-1] + ["../outside-report.html"], cwd=root, capture_output=True)
            self.assertEqual(2, outside.returncode)
            self.assertFalse((root.parent / "outside-report.html").exists())
            self.data["summary"] = "Versión cambiada"
            source.write_text(json.dumps(self.data), encoding="utf-8")
            forced = subprocess.run(argv + ["--force"], cwd=root, capture_output=True)
            self.assertEqual(0, forced.returncode)
            self.assertIn("Versión cambiada", output.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
