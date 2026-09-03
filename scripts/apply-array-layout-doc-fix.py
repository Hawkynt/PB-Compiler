from pathlib import Path

replacements = {
    Path("docs/IR.md"): [
        (
            "- static arrays (1-D and multi-dimensional, row-major byte GEP); **string arrays**",
            "- static arrays (1-D and multi-dimensional, PowerBASIC first-subscript-fastest byte GEP); **string arrays**",
        ),
        (
            "  knows. Element access is row-major flattened relative to the bounds;",
            "  knows. Element access uses PowerBASIC's first-subscript-fastest flattening relative to the bounds;",
        ),
    ],
    Path("PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs"): [
        (
            "  /// A rank-2 INTEGER array whose row-major index is a runtime product, written by nested counters.",
            "  /// A rank-2 INTEGER array whose flattened index is a runtime product, written by nested counters.",
        ),
    ],
}

for path, pairs in replacements.items():
    text = path.read_text(encoding="utf-8")
    for old, new in pairs:
        count = text.count(old)
        if count != 1:
            raise SystemExit(f"{path}: expected exactly one match, found {count}: {old!r}")
        text = text.replace(old, new)
    path.write_text(text, encoding="utf-8")
