#!/usr/bin/env python3
"""
画面の JavaScript の網羅率（docs/design/desktop/desktop-blazor-hybrid.md の 16.7）。
Playwright のテスト（BrowserHost、環境変数 TT_JS_COVERAGE_DIR）が書いた Chromium の CDP の Profiler.takePreciseCoverage の結果をまとめ、
対象のファイルの中で、どのテストでも通っていないブロック（関数・if/else・?:・&&・||・?? の側など。V8 の block coverage）を数える。
文字の位置ごとに、それを含むいちばん内側の範囲の回数で通ったかを決め、空白・コメント・かっこだけの所は数えない。
使い方: js-coverage.py <結果のフォルダー> <対象のファイル> [--threshold 100]
"""
import json
import re
import sys
from pathlib import Path


def main() -> int:
    args = sys.argv[1:]
    threshold = 100.0
    if "--threshold" in args:
        i = args.index("--threshold")
        threshold = float(args[i + 1])
        del args[i:i + 2]
    directory, target = Path(args[0]), Path(args[1])
    source = target.read_text(encoding="utf-8")
    name = target.name
    covered = [False] * len(source)
    seen = 0
    for path in sorted(directory.glob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        for script in data.get("result", []):
            if not script.get("url", "").split("?")[0].endswith("/" + name):
                continue
            ranges = [r for f in script["functions"] for r in f["ranges"]]
            top = max(r["endOffset"] for r in ranges)
            if top != len(source):
                print(f"::error::{path.name}: {name} の長さが違います（配られたもの {top}・リポジトリ {len(source)}）")
                return 1
            seen += 1
            # 外側の範囲から順に当て、内側の範囲で上書きする（いちばん内側の回数）
            counts = [0] * len(source)
            for r in sorted(ranges, key=lambda r: (r["startOffset"], -r["endOffset"])):
                for i in range(r["startOffset"], r["endOffset"]):
                    counts[i] = r["count"]
            for i, c in enumerate(counts):
                if c > 0:
                    covered[i] = True
    if seen == 0:
        print(f"::error::{name} の網羅率の結果がありません（TT_JS_COVERAGE_DIR を付けてテストを動かしたか）")
        return 1
    # 数える文字: 空白・コメント・区切りの記号だけの所は除く
    code = [True] * len(source)
    for m in re.finditer(r"//[^\n]*|/\*.*?\*/", source, re.S):
        for i in range(m.start(), m.end()):
            code[i] = False
    for i, ch in enumerate(source):
        if ch.isspace() or ch in "{}();,":
            code[i] = False
    total = sum(code)
    hit = sum(1 for i in range(len(source)) if code[i] and covered[i])
    percent = 100.0 * hit / total
    print(f"{name}: 通った {hit} / {total} 文字（{percent:.2f}%、ページ {seen} 回分）")
    # 通っていない所を行ごとに出す
    line_starts = [0] + [m.end() for m in re.finditer("\n", source)]
    missed = {}
    for i in range(len(source)):
        if code[i] and not covered[i]:
            line = max(n for n, start in enumerate(line_starts) if start <= i) + 1
            missed.setdefault(line, []).append(source[i])
    for line, chars in sorted(missed.items()):
        print(f"  {line} 行目: 通っていない「{''.join(chars)[:80]}」 ← {source.splitlines()[line - 1].strip()[:100]}")
    if percent + 1e-9 < threshold:
        print(f"::error::{name} の網羅率 {percent:.2f}% が {threshold}% に届きません")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
