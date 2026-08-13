#!/usr/bin/env python3
"""Render the manual DOCX with the Codex Documents renderer.

This wrapper deliberately avoids re-laying out the document in ReportLab.
The PDF and QA page images must come from the same DOCX rendering pass.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
from xml.etree import ElementTree
from zipfile import ZIP_DEFLATED, ZipFile


APP_PROPERTIES_NAMESPACE = (
    "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"
)


def find_renderer(explicit: Path | None) -> Path:
    if explicit is not None:
        renderer = explicit.expanduser().resolve()
        if renderer.is_file():
            return renderer
        raise FileNotFoundError(f"render_docx.py was not found: {renderer}")

    from_environment = os.environ.get("CODEX_DOCUMENT_RENDERER")
    if from_environment:
        renderer = Path(from_environment).expanduser().resolve()
        if renderer.is_file():
            return renderer
        raise FileNotFoundError(
            f"CODEX_DOCUMENT_RENDERER does not point to a file: {renderer}"
        )

    runtime_root = (
        Path.home()
        / ".codex"
        / "plugins"
        / "cache"
        / "openai-primary-runtime"
        / "documents"
    )
    candidates = sorted(
        runtime_root.glob("*/skills/documents/render_docx.py"),
        reverse=True,
    )
    if candidates:
        return candidates[0].resolve()
    raise FileNotFoundError(
        "Codex Documents render_docx.py was not found. "
        "Pass --render-script or set CODEX_DOCUMENT_RENDERER."
    )


def render_docx(
    input_path: Path,
    output_path: Path,
    render_dir: Path,
    renderer: Path,
) -> list[Path]:
    input_path = input_path.resolve()
    output_path = output_path.resolve()
    render_dir = render_dir.resolve()
    if not input_path.is_file():
        raise FileNotFoundError(f"Input DOCX was not found: {input_path}")
    if input_path.suffix.lower() != ".docx":
        raise ValueError("--input must be a DOCX file")
    if output_path.suffix.lower() != ".pdf":
        raise ValueError("--output must end in .pdf")

    render_dir.mkdir(parents=True, exist_ok=True)
    for stale_page in render_dir.glob("page-*.png"):
        stale_page.unlink()
    emitted_pdf = render_dir / f"{input_path.stem}.pdf"
    if emitted_pdf.is_file():
        emitted_pdf.unlink()
    command = [
        sys.executable,
        str(renderer),
        str(input_path),
        "--output_dir",
        str(render_dir),
        "--emit_pdf",
    ]
    renderer_error: subprocess.CalledProcessError | None = None
    try:
        subprocess.run(command, check=True, capture_output=True, text=True)
    except subprocess.CalledProcessError as error:
        renderer_error = error

    if not emitted_pdf.is_file() or emitted_pdf.stat().st_size == 0:
        render_with_word_and_pymupdf(input_path, emitted_pdf, render_dir)
        if renderer_error is not None:
            print(
                "WARNING: bundled render_docx.py was unavailable; "
                "used installed Microsoft Word plus PyMuPDF for PDF/PNG QA.",
                file=sys.stderr,
            )

    output_path.parent.mkdir(parents=True, exist_ok=True)
    if emitted_pdf != output_path:
        shutil.copy2(emitted_pdf, output_path)

    pages = sorted(render_dir.glob("page-*.png"))
    if not pages:
        raise RuntimeError(f"Renderer did not emit QA page images in {render_dir}")
    update_docx_page_count(input_path, len(pages))
    return pages


def update_docx_page_count(input_path: Path, page_count: int) -> None:
    """Synchronize the DOCX extended Pages property with the verified render."""
    if page_count < 1:
        raise ValueError("page_count must be positive")

    app_part = "docProps/app.xml"
    with ZipFile(input_path, "r") as source:
        if app_part not in source.namelist():
            raise RuntimeError(f"DOCX extended properties were not found: {app_part}")
        app_xml = source.read(app_part)
        root = ElementTree.fromstring(app_xml)
        pages = root.find(f"{{{APP_PROPERTIES_NAMESPACE}}}Pages")
        if pages is None:
            raise RuntimeError("DOCX extended Pages property was not found")
        if pages.text == str(page_count):
            return
        pages.text = str(page_count)
        updated_app_xml = ElementTree.tostring(
            root,
            encoding="utf-8",
            xml_declaration=True,
        )
        package_items = [
            (item, source.read(item.filename))
            for item in source.infolist()
        ]

    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{input_path.name}.",
        suffix=".tmp",
        dir=input_path.parent,
    )
    os.close(descriptor)
    temporary_path = Path(temporary_name)
    try:
        with ZipFile(temporary_path, "w", compression=ZIP_DEFLATED) as target:
            for item, data in package_items:
                target.writestr(
                    item,
                    updated_app_xml if item.filename == app_part else data,
                )
        os.replace(temporary_path, input_path)
    finally:
        temporary_path.unlink(missing_ok=True)


def render_with_word_and_pymupdf(
    input_path: Path,
    emitted_pdf: Path,
    render_dir: Path,
) -> None:
    try:
        import pythoncom
        import win32com.client
    except ImportError as error:
        raise RuntimeError(
            "Bundled renderer failed and Microsoft Word automation is unavailable."
        ) from error

    pythoncom.CoInitialize()
    word = None
    document = None
    try:
        word = win32com.client.DispatchEx("Word.Application")
        word.Visible = False
        word.DisplayAlerts = 0
        document = word.Documents.Open(
            str(input_path),
            ConfirmConversions=False,
            ReadOnly=True,
            AddToRecentFiles=False,
            Visible=False,
        )
        document.Repaginate()
        document.Fields.Update()
        document.ExportAsFixedFormat(
            OutputFileName=str(emitted_pdf),
            ExportFormat=17,
            OpenAfterExport=False,
            OptimizeFor=0,
            Range=0,
            Item=0,
            IncludeDocProps=True,
            KeepIRM=True,
            CreateBookmarks=1,
            DocStructureTags=True,
            BitmapMissingFonts=True,
            UseISO19005_1=False,
        )
    finally:
        if document is not None:
            document.Close(SaveChanges=False)
        if word is not None:
            word.Quit(SaveChanges=False)
        pythoncom.CoUninitialize()

    deadline = time.monotonic() + 10
    while (
        (not emitted_pdf.is_file() or emitted_pdf.stat().st_size == 0)
        and time.monotonic() < deadline
    ):
        time.sleep(0.1)
    if not emitted_pdf.is_file() or emitted_pdf.stat().st_size == 0:
        raise RuntimeError(f"Microsoft Word did not emit a usable PDF: {emitted_pdf}")

    try:
        import pymupdf
    except ImportError:
        import fitz as pymupdf

    # Word's PDF export can encode Korean document properties with the active
    # ANSI code page. Reapply the DOCX core properties as Unicode metadata.
    from docx import Document

    core = Document(str(input_path)).core_properties
    pdf = pymupdf.open(str(emitted_pdf))
    try:
        metadata = dict(pdf.metadata or {})
        metadata.update(
            {
                "title": core.title or "",
                "author": core.author or "",
                "subject": core.subject or "",
                "keywords": core.keywords or "",
            }
        )
        pdf.set_metadata(metadata)
        pdf.saveIncr()
        for index, page in enumerate(pdf, start=1):
            pixmap = page.get_pixmap(matrix=pymupdf.Matrix(2, 2), alpha=False)
            pixmap.save(str(render_dir / f"page-{index}.png"))
    finally:
        pdf.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--render-dir", type=Path, required=True)
    parser.add_argument("--render-script", type=Path)
    args = parser.parse_args()

    renderer = find_renderer(args.render_script)
    pages = render_docx(
        args.input,
        args.output,
        args.render_dir,
        renderer,
    )
    print(args.output.resolve())
    print(f"QA pages: {len(pages)}")


if __name__ == "__main__":
    main()
