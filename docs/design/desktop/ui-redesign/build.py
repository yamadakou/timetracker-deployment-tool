# 画面イメージ（デザイン案 A・B・C × デプロイの一覧・新しくデプロイ）の HTML を作る。python3 build.py → *.html。画像は shot.js
import html
E = html.escape
ROWS = [
    ("開発用", "rg-sales-demo", "sales", "営業", "稼働中", "ok", "11/16", 3, "10/15", ""),
    ("開発用", "rg-training", "training", "教育", "稼働中", "ok", "12/20", 37, "11/01", ""),
    ("検証用", "rg-qa-01", "qa01", "品質保証", "停止", "muted", "期限なし", None, "09/30", ""),
    ("検証用", "rg-qa-02", "qa02", "品質保証", "デプロイ中", "info", "12/31", 48, "—", "デプロイ 実行中 62%"),
    ("開発用", "rg-poc-ai", "poc", "", "作りかけ", "warn", "11/14", 1, "11/12", "削除 予定 11/14 18:00"),
    ("開発用", "rg-old-team", "", "", "リソースグループだけ", "muted", "—", None, "—", ""),
    ("検証用", "rg-review", "review", "レビュー", "稼働中", "ok", "01/31", 79, "11/05", ""),
]
ICON = {
    "signin": '<path d="M10 17l5-5-5-5M15 12H3M21 3v18"/>',
    "list": '<path d="M4 6h16M4 12h16M4 18h10"/>',
    "plus": '<path d="M12 5v14M5 12h14"/>',
    "jobs": '<path d="M12 8v4l3 2"/><circle cx="12" cy="12" r="9"/>',
    "flask": '<path d="M9 3h6M10 3v6L5 19a1 1 0 0 0 1 2h12a1 1 0 0 0 1-2l-5-10V3"/>',
    "gear": '<circle cx="12" cy="12" r="3"/><path d="M19 12a7 7 0 0 0-.1-1.2l2-1.6-2-3.4-2.4 1a7 7 0 0 0-2-1.2L14 3h-4l-.5 2.6a7 7 0 0 0-2 1.2l-2.4-1-2 3.4 2 1.6A7 7 0 0 0 5 12c0 .4 0 .8.1 1.2l-2 1.6 2 3.4 2.4-1a7 7 0 0 0 2 1.2L10 21h4l.5-2.6a7 7 0 0 0 2-1.2l2.4 1 2-3.4-2-1.6c.1-.4.1-.8.1-1.2z"/>',
    "search": '<circle cx="11" cy="11" r="7"/><path d="M21 21l-4.3-4.3"/>',
    "refresh": '<path d="M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7"/>',
    "trash": '<path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13"/>',
    "filter": '<path d="M4 5h16l-6 8v6l-4-2v-4z"/>',
    "link": '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
    "key": '<circle cx="8" cy="15" r="4"/><path d="M11 12l9-9M17 6l3 3"/>',
    "check": '<path d="M5 12l5 5L20 7"/>',
    "clock": '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    "cloud": '<path d="M7 18a5 5 0 1 1 1-9.9A6 6 0 0 1 19 10a4 4 0 0 1-1 8z"/>',
    "chev": '<path d="M9 6l6 6-6 6"/>',
    "dots": '<circle cx="5" cy="12" r="1.5"/><circle cx="12" cy="12" r="1.5"/><circle cx="19" cy="12" r="1.5"/>',
    "folder": '<path d="M3 7h6l2 2h10v10H3z"/>',
    "bell": '<path d="M6 16V11a6 6 0 0 1 12 0v5l2 2H4zM10 20h4"/>',
}
def ic(name, size=18, cls="ic"):
    return f'<svg class="{cls}" width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">{ICON[name]}</svg>'
NAV = [("signin", "サインイン"), ("list", "デプロイの一覧"), ("plus", "新しくデプロイ"), ("jobs", "ジョブの一覧"), ("flask", "ローカル検証")]
BASE = """
@font-face { font-family: 'NotoJP'; src: local('Noto Sans JP'); }
* { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: 'Inter', 'Noto Sans JP', sans-serif; font-size: 13px; width: 1440px; height: 900px; overflow: hidden; -webkit-font-smoothing: antialiased; }
.ic { flex: none; }
table { border-collapse: collapse; width: 100%; }
.mono { font-family: 'Inter', monospace; font-variant-numeric: tabular-nums; }
"""
def page(title, css, body):
    return f'<!doctype html><html lang="ja"><head><meta charset="utf-8"><title>{E(title)}</title><style>{BASE}{css}</style></head><body>{body}</body></html>'

# ---------------- 案 A: Fluent（Windows 11 の流儀） ----------------
A_CSS = """
:root { --bg:#f3f3f3; --card:#ffffff; --line:#e5e5e5; --text:#1b1b1b; --sub:#5f5f5f; --accent:#005fb8; --accent-soft:#e6f0fa; }
body { background: linear-gradient(135deg,#eef2f7,#f6f3f1); color: var(--text); display:flex; }
.title { position:absolute; top:0; left:0; right:0; height:40px; display:flex; align-items:center; gap:10px; padding:0 16px; font-size:12px; color:#333; }
.logo { width:18px; height:18px; border-radius:4px; background:linear-gradient(135deg,#0078d4,#4cc2ff); }
.nav { width:212px; flex:none; padding:52px 8px 12px; display:flex; flex-direction:column; gap:2px; height:900px; }
.nav a { display:flex; align-items:center; gap:12px; padding:9px 12px; border-radius:6px; color:#1b1b1b; text-decoration:none; font-size:13.5px; position:relative; }
.nav a.on { background: rgba(0,0,0,.055); font-weight:600; }
.nav a.on::before { content:""; position:absolute; left:0; top:10px; bottom:10px; width:3px; border-radius:2px; background:var(--accent); }
.nav .sp { flex:1; }
.acct { display:flex; align-items:center; gap:10px; padding:10px 12px; border-radius:8px; }
.avatar { width:30px; height:30px; border-radius:50%; background:#c7dcf3; color:#004c8f; display:grid; place-items:center; font-weight:700; font-size:12px; }
.main { flex:1; padding:52px 24px 20px 8px; height:900px; display:flex; flex-direction:column; gap:14px; }
.surface { background:var(--card); border:1px solid var(--line); border-radius:8px; box-shadow:0 2px 6px rgba(0,0,0,.04); }
h1 { font-size:28px; font-weight:600; letter-spacing:-.2px; }
.head { display:flex; align-items:flex-end; justify-content:space-between; }
.sub { color:var(--sub); font-size:12.5px; margin-top:4px; }
.cmd { display:flex; align-items:center; gap:4px; padding:6px; }
.btn { display:inline-flex; align-items:center; gap:8px; height:32px; padding:0 12px; border-radius:5px; border:1px solid #d1d1d1; background:#fbfbfb; color:#1b1b1b; font-size:13px; white-space:nowrap; }
.btn.primary { background:var(--accent); border-color:var(--accent); color:#fff; }
.btn.ghost { border-color:transparent; background:transparent; }
.btn.danger { color:#c42b1c; }
.sep { width:1px; height:20px; background:var(--line); margin:0 4px; }
.search { margin-left:auto; display:flex; align-items:center; gap:8px; width:300px; height:32px; padding:0 10px; border:1px solid #d1d1d1; border-bottom:2px solid #8a8a8a; border-radius:5px; background:#fff; color:#777; }
.content { flex:1; display:flex; gap:14px; min-height:0; }
.grid { flex:1; overflow:hidden; display:flex; flex-direction:column; }
.grid { min-width:0; } .grid table { table-layout:fixed; } .grid th, .grid td { white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.acct > div:last-child { min-width:0; } .acct > div:last-child div:first-child { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.grid th { text-align:left; font-weight:600; font-size:12px; color:#444; padding:10px 12px; border-bottom:1px solid var(--line); background:#fafafa; }
.grid td { padding:11px 12px; border-bottom:1px solid #f0f0f0; vertical-align:middle; }
.grid tr.sel td { background:var(--accent-soft); }
.grid tr.sel td:first-child { box-shadow: inset 3px 0 0 var(--accent); }
.cb { width:16px; height:16px; border:1px solid #8a8a8a; border-radius:3px; display:inline-block; vertical-align:middle; }
.cb.on { background:var(--accent); border-color:var(--accent); position:relative; }
.cb.on::after { content:""; position:absolute; left:4.5px; top:1.5px; width:4px; height:8px; border:solid #fff; border-width:0 2px 2px 0; transform:rotate(45deg); }
.badge { display:inline-flex; align-items:center; gap:6px; font-size:12px; }
.dot { width:8px; height:8px; border-radius:50%; }
.ok .dot { background:#0f7b0f; } .muted .dot { background:#8a8a8a; } .info .dot { background:#005fb8; } .warn .dot { background:#c27c00; }
.exp-soon { color:#c42b1c; font-weight:600; }
.pager { display:flex; align-items:center; justify-content:space-between; padding:10px 14px; color:var(--sub); font-size:12px; margin-top:auto; border-top:1px solid var(--line); }
.detail { width:316px; flex:none; padding:18px; display:flex; flex-direction:column; gap:14px; }
.detail h2 { font-size:18px; font-weight:600; }
.kv { display:grid; grid-template-columns: 96px 1fr; gap:8px 10px; font-size:12.5px; }
.kv dt { color:var(--sub); }
.info-bar { display:flex; gap:10px; align-items:flex-start; background:#fff4ce; border:1px solid #f5e1a4; padding:10px 12px; border-radius:6px; font-size:12.5px; }
.chip { display:inline-block; padding:2px 8px; border-radius:10px; background:#eef0f3; font-size:11.5px; color:#333; }
.jobtag { font-size:11.5px; color:#005fb8; background:#e6f0fa; padding:2px 8px; border-radius:10px; }
.stepper { display:flex; gap:0; padding:18px 22px; align-items:center; }
.step { display:flex; align-items:center; gap:10px; color:var(--sub); }
.step .n { width:26px; height:26px; border-radius:50%; border:1.5px solid #c8c8c8; display:grid; place-items:center; font-weight:600; font-size:12px; background:#fff; }
.step.done .n { background:var(--accent); border-color:var(--accent); color:#fff; }
.step.cur { color:var(--text); font-weight:600; } .step.cur .n { border-color:var(--accent); color:var(--accent); box-shadow:0 0 0 3px #cfe3f7; }
.bar { flex:1; height:2px; background:#ddd; margin:0 14px; } .bar.done { background:var(--accent); }
.form { padding:22px 24px; display:grid; grid-template-columns: 1fr 1fr; gap:18px 22px; }
.field label { display:block; font-size:12.5px; margin-bottom:6px; font-weight:500; }
.input { height:34px; border:1px solid #d1d1d1; border-bottom:2px solid #8a8a8a; border-radius:5px; background:#fff; display:flex; align-items:center; padding:0 10px; justify-content:space-between; }
.input.focus { border-bottom-color:var(--accent); }
.hint { color:var(--sub); font-size:11.5px; margin-top:5px; }
.err { color:#c42b1c; font-size:11.5px; margin-top:5px; }
.seg { display:inline-flex; border:1px solid #d1d1d1; border-radius:6px; overflow:hidden; }
.seg span { padding:7px 14px; font-size:12.5px; } .seg span.on { background:var(--accent); color:#fff; }
.toggle { width:40px; height:20px; border-radius:10px; background:var(--accent); position:relative; display:inline-block; vertical-align:middle; }
.toggle::after { content:""; position:absolute; right:3px; top:3px; width:14px; height:14px; border-radius:50%; background:#fff; }
.footer { display:flex; justify-content:space-between; align-items:center; padding:14px 24px; border-top:1px solid var(--line); }
.summary { width:320px; padding:18px; display:flex; flex-direction:column; gap:12px; }
.summary h3 { font-size:14px; font-weight:600; }
.sumrow { display:flex; justify-content:space-between; font-size:12.5px; padding:6px 0; border-bottom:1px dashed #eee; }
.sumrow span:first-child { color:var(--sub); }
"""
def a_shell(active, main):
    nav = "".join(f'<a class="{"on" if i == active else ""}">{ic(k)}{E(t)}</a>' for i, (k, t) in enumerate(NAV))
    return f'''<div class="title"><div class="logo"></div>TTDeployTool</div>
<nav class="nav">{nav}<div class="sp"></div><a>{ic("gear")}設定</a>
<div class="acct"><div class="avatar">KY</div><div><div style="font-weight:600">mock-user@example.com</div><div class="sub" style="margin:0">サブスクリプション 2 件</div></div></div></nav>
<main class="main">{main}</main>'''
def a_list():
    rows = ""
    for i, (sub, rg, app, grp, st, tone, exp, days, dep, job) in enumerate(ROWS):
        sel = i == 0
        exp_html = f'<span class="{"exp-soon" if days is not None and days <= 3 else ""}">{E(exp)}</span>' + (f' <span class="sub" style="display:inline">（あと {days} 日）</span>' if days is not None and days <= 7 else "")
        rows += f'''<tr class="{"sel" if sel else ""}"><td><span class="cb {"on" if sel else ""}"></span></td>
<td>{E(app) if app else '<span class="sub" style="display:inline">—</span>'}</td><td>{E(rg)}</td><td>{E(sub)}</td>
<td>{f'<span class="chip">{E(grp)}</span>' if grp else ""}</td><td><span class="badge {tone}"><span class="dot"></span>{E(st)}</span></td>
<td>{exp_html}</td><td>{f'<span class="jobtag">{E(job)}</span>' if job else ""}</td></tr>'''
    main = f'''<div class="head"><div><h1>デプロイの一覧</h1><div class="sub">本ツールでデプロイした環境（2 サブスクリプション・7 件）。最終更新 11/13 15:42</div></div></div>
<div class="surface cmd"><span class="btn primary">{ic("plus",16)}新しくデプロイ</span><span class="sep"></span>
<span class="btn ghost">{ic("refresh",16)}読み直す</span><span class="btn ghost">{ic("clock",16)}期限を変える</span><span class="btn ghost danger">{ic("trash",16)}Azure から削除…</span><span class="btn ghost">{ic("filter",16)}絞り込み</span>
<div class="search">{ic("search",16)}アプリ名・グループで探す</div></div>
<div class="content"><div class="surface grid"><table><thead><tr><th style="width:40px"><span class="cb"></span></th><th style="width:84px">アプリ名</th><th style="width:124px">リソースグループ</th><th style="width:96px">サブスクリプション</th><th style="width:86px">グループ</th><th style="width:150px">状態</th><th style="width:140px">利用可能期限</th><th>ジョブ</th></tr></thead><tbody>{rows}</tbody></table>
<div class="pager"><span>1〜7 件目（全 7 件）</span><span>20 件ずつ ▾</span></div></div>
<aside class="surface detail"><div><div class="sub" style="margin:0 0 4px">rg-sales-demo</div><h2>sales</h2></div>
<div class="info-bar">{ic("bell",18)}<div>利用可能期限は <b>11/16（あと 3 日）</b>です。期限を過ぎると自動で削除します。</div></div>
<dl class="kv"><dt>URL</dt><dd style="color:var(--accent)">sales-tt.japaneast.…</dd><dt>状態</dt><dd><span class="badge ok"><span class="dot"></span>稼働中（3/3）</span></dd>
<dt>DB</dt><dd>PostgreSQL・Azure Files に永続化</dd><dt>スペック</dt><dd>TT 1.5 CPU / 3 GiB</dd><dt>グループ</dt><dd><span class="chip">営業</span></dd><dt>デプロイ日</dt><dd>2026/10/15</dd></dl>
<div style="display:flex;flex-wrap:wrap;gap:8px"><span class="btn primary">{ic("link",16)}Timetracker を開く</span><span class="btn">{ic("key",16)}パスワード</span><span class="btn">{ic("cloud",16)}状態を確認</span><span class="btn">{ic("refresh",16)}この設定で新しくデプロイ</span></div></aside></div>'''
    return page("案 A デプロイの一覧", A_CSS, a_shell(1, main))
def a_wizard():
    steps = [("対象", "done"), ("DB", "done"), ("パスワード", "done"), ("スペックと期限", "cur"), ("確認と実行", "")]
    st = ""
    for i, (t, c) in enumerate(steps):
        st += f'<div class="step {c}"><span class="n">{ic("check",14) if c == "done" else i + 1}</span>{E(t)}</div>'
        if i < len(steps) - 1:
            st += f'<div class="bar {"done" if c == "done" else ""}"></div>'
    main = f'''<div class="head"><div><h1>新しくデプロイ</h1><div class="sub">Timetracker の環境を Azure に作ります。入力は次へ進むたびに確かめます。</div></div><span class="btn">テンプレートから入力 ▾</span></div>
<div class="surface stepper">{st}</div>
<div class="content"><div class="surface" style="flex:1;display:flex;flex-direction:column"><div class="form">
<div class="field"><label>Timetracker の CPU</label><div class="input">1.5<span class="sub" style="margin:0">vCPU</span></div><div class="hint">0.25 刻み。既定は 1.5</div></div>
<div class="field"><label>Timetracker のメモリ</label><div class="input">3.0<span class="sub" style="margin:0">GiB</span></div></div>
<div class="field"><label>DB の CPU / メモリ</label><div class="input">0.5 vCPU / 1.0 GiB<span>▾</span></div></div>
<div class="field"><label>Redis の CPU / メモリ</label><div class="input">0.25 vCPU / 0.5 GiB<span>▾</span></div></div>
<div class="field" style="grid-column:1/-1"><label>利用可能期限</label><div class="seg"><span>既定（32 日後）</span><span class="on">日付を指定</span><span>期限なし</span></div>
<div style="display:flex;gap:12px;margin-top:10px;align-items:center"><div class="input focus" style="width:220px">2026/11/02<span>{ic("clock",16)}</span></div><div class="err">利用可能期限日（2026-11-02）は、今日以降の日付を指定してください。</div></div></div>
<div class="field"><label>グループ</label><div class="input">営業<span>▾</span></div><div class="hint">デプロイの一覧で、グループごとに絞り込めます</div></div>
<div class="field"><label>DB のデータを残す</label><div style="display:flex;align-items:center;gap:10px;height:34px"><span class="toggle"></span>Azure Files に永続化する</div></div>
</div><div class="footer" style="margin-top:auto"><span class="btn">戻る</span><span class="btn primary" style="opacity:.5">次へ（確認と実行）</span></div></div>
<aside class="surface summary"><h3>入力のまとめ</h3>
<div class="sumrow"><span>サブスクリプション</span><span>開発用</span></div><div class="sumrow"><span>リソースグループ</span><span>rg-sales-demo</span></div><div class="sumrow"><span>アプリ名</span><span>sales</span></div>
<div class="sumrow"><span>リージョン</span><span>japaneast</span></div><div class="sumrow"><span>DB</span><span>PostgreSQL</span></div><div class="sumrow"><span>パスワード</span><span>入力済み（2）</span></div>
<div class="info-bar" style="margin-top:6px">{ic("refresh",18)}<div>失敗したジョブ（11/02 10:50）の設定を読み込みました。期限を直してください。</div></div></aside></div>'''
    return page("案 A 新しくデプロイ", A_CSS, a_shell(2, main))

# ---------------- 案 B: ダッシュボード（状況が一目で分かる） ----------------
B_CSS = """
:root { --bg:#f6f7fb; --card:#fff; --line:#e7e9f0; --text:#172033; --sub:#66708a; --accent:#4f46e5; --accent-soft:#eef0ff; }
body { background:var(--bg); color:var(--text); display:flex; }
.rail { width:76px; background:#141a2e; height:900px; display:flex; flex-direction:column; align-items:center; padding:18px 0; gap:8px; }
.rail .logo { width:38px; height:38px; border-radius:11px; background:linear-gradient(135deg,#6366f1,#22d3ee); margin-bottom:14px; display:grid; place-items:center; color:#fff; font-weight:800; font-size:14px; }
.rail a { width:52px; padding:8px 0 6px; border-radius:12px; color:#9aa4c2; display:flex; flex-direction:column; align-items:center; gap:4px; font-size:9.5px; }
.rail a.on { background:#2a3352; color:#fff; }
.rail .sp { flex:1; }
.main { flex:1; display:flex; flex-direction:column; height:900px; }
.top { height:64px; background:#fff; border-bottom:1px solid var(--line); display:flex; align-items:center; padding:0 28px; gap:16px; }
.top h1 { font-size:18px; font-weight:700; }
.crumb { color:var(--sub); font-size:12.5px; }
.search { margin-left:auto; width:360px; height:38px; border-radius:10px; background:#f1f3f8; display:flex; align-items:center; gap:10px; padding:0 14px; color:#8a93ab; }
.kbd { margin-left:auto; font-size:11px; border:1px solid #d7dbe6; border-radius:5px; padding:1px 6px; color:#7a839b; background:#fff; }
.avatar { width:36px; height:36px; border-radius:50%; background:#e0e7ff; color:#4338ca; display:grid; place-items:center; font-weight:700; }
.body { padding:22px 28px; display:flex; flex-direction:column; gap:18px; flex:1; min-height:0; }
.kpis { display:grid; grid-template-columns: repeat(4, 1fr); gap:16px; }
.kpi { background:#fff; border:1px solid var(--line); border-radius:14px; padding:16px 18px; display:flex; gap:14px; align-items:center; }
.kpi .ico { width:42px; height:42px; border-radius:12px; display:grid; place-items:center; }
.kpi .v { font-size:24px; font-weight:700; } .kpi .l { color:var(--sub); font-size:12px; }
.card { background:#fff; border:1px solid var(--line); border-radius:14px; }
.tools { display:flex; align-items:center; gap:10px; padding:14px 16px; border-bottom:1px solid var(--line); }
.pill { padding:6px 12px; border-radius:20px; background:#f1f3f8; color:#3b4560; font-size:12.5px; display:inline-flex; gap:6px; align-items:center; }
.pill.on { background:var(--accent); color:#fff; }
.pill .n { font-size:11px; opacity:.8; }
.btn { display:inline-flex; align-items:center; gap:8px; height:36px; padding:0 14px; border-radius:10px; border:1px solid var(--line); background:#fff; font-size:13px; font-weight:500; white-space:nowrap; }
.btn.primary { background:var(--accent); color:#fff; border-color:var(--accent); box-shadow:0 4px 12px rgba(79,70,229,.25); }
.wrap { display:flex; gap:18px; flex:1; min-height:0; }
.list { flex:1; overflow:hidden; display:flex; flex-direction:column; }
.list th { text-align:left; font-size:11.5px; color:var(--sub); font-weight:600; padding:10px 16px; text-transform:none; background:#fafbfe; border-bottom:1px solid var(--line); }
.list td { padding:12px 16px; border-bottom:1px solid #f1f2f6; }
.list tr.sel td { background:var(--accent-soft); }
.appcell { display:flex; gap:12px; align-items:center; }
.av { width:34px; height:34px; border-radius:10px; display:grid; place-items:center; font-weight:700; color:#fff; font-size:13px; }
.status { display:inline-flex; align-items:center; gap:6px; padding:4px 10px; border-radius:20px; font-size:12px; font-weight:500; }
.s-ok { background:#e7f8ef; color:#137a45; } .s-muted { background:#eef0f4; color:#59627a; } .s-info { background:#e8efff; color:#3150c9; } .s-warn { background:#fff3e0; color:#b45f06; }
.exp { width:150px; } .expbar { height:6px; border-radius:3px; background:#eef0f4; margin-top:6px; overflow:hidden; } .expbar i { display:block; height:100%; border-radius:3px; }
.drawer { width:380px; display:flex; flex-direction:column; }
.drawer .hd { padding:20px; border-bottom:1px solid var(--line); display:flex; gap:14px; align-items:center; }
.drawer .sec { padding:16px 20px; border-bottom:1px solid var(--line); }
.drawer h4 { font-size:12px; color:var(--sub); font-weight:600; margin-bottom:10px; }
.apps { display:flex; flex-direction:column; gap:8px; }
.appline { display:flex; justify-content:space-between; align-items:center; padding:8px 10px; background:#fafbfe; border-radius:8px; font-size:12.5px; }
.timeline { display:flex; flex-direction:column; gap:12px; }
.tl { display:flex; gap:10px; font-size:12.5px; } .tl .d { width:10px; height:10px; border-radius:50%; margin-top:4px; }
.vstep { display:flex; flex-direction:column; gap:4px; padding:22px 18px; width:260px; }
.vs { display:flex; gap:12px; align-items:center; padding:12px; border-radius:12px; color:var(--sub); }
.vs .n { width:30px; height:30px; border-radius:10px; display:grid; place-items:center; background:#f1f3f8; font-weight:700; }
.vs.done .n { background:#e7f8ef; color:#137a45; } .vs.cur { background:var(--accent-soft); color:var(--text); font-weight:600; } .vs.cur .n { background:var(--accent); color:#fff; }
.fgrid { display:grid; grid-template-columns: 1fr 1fr; gap:18px; padding:24px; }
.f label { display:block; font-size:12.5px; font-weight:600; margin-bottom:8px; }
.inp { height:40px; border-radius:10px; border:1px solid #dfe3ec; display:flex; align-items:center; justify-content:space-between; padding:0 12px; background:#fff; }
.inp.bad { border-color:#e5484d; box-shadow:0 0 0 3px #fde4e4; }
.radio { display:flex; gap:10px; }
.ropt { flex:1; border:1px solid #dfe3ec; border-radius:12px; padding:12px; font-size:12.5px; } .ropt.on { border-color:var(--accent); background:var(--accent-soft); box-shadow:0 0 0 2px #c7c9ff inset; }
.ropt b { display:block; margin-bottom:4px; }
.slider { height:6px; border-radius:3px; background:#e4e7ef; position:relative; margin:16px 4px 6px; } .slider i { position:absolute; left:0; top:0; bottom:0; width:45%; background:var(--accent); border-radius:3px; } .slider b { position:absolute; left:45%; top:-6px; width:18px; height:18px; border-radius:50%; background:#fff; border:3px solid var(--accent); transform:translateX(-50%); }
.preview { width:330px; display:flex; flex-direction:column; }
.cost { font-size:26px; font-weight:700; }
"""
COLORS = ["#6366f1", "#0ea5e9", "#10b981", "#f59e0b", "#ef4444", "#94a3b8", "#8b5cf6"]
def b_shell(active, title, crumb, body):
    rail = "".join(f'<a class="{"on" if i == active else ""}">{ic(k,20)}<span>{E(t.replace("デプロイの","").replace("ジョブの","ジョブ").replace("新しく","新規")[:6])}</span></a>' for i, (k, t) in enumerate(NAV))
    return f'''<nav class="rail"><div class="logo">TT</div>{rail}<div class="sp"></div><a>{ic("bell",20)}<span>通知</span></a><a>{ic("gear",20)}<span>設定</span></a></nav>
<div class="main"><div class="top"><div><div class="crumb">{E(crumb)}</div><h1>{E(title)}</h1></div><div class="search">{ic("search",16)}デプロイ・ジョブ・操作を探す<span class="kbd">Ctrl K</span></div><div class="avatar">KY</div></div>
<div class="body">{body}</div></div>'''
def b_list():
    kpi = [("cloud", "#eef0ff", "#4f46e5", "7", "デプロイ（2 サブスクリプション）"), ("clock", "#fff1f2", "#e11d48", "2", "期限が 7 日以内"), ("jobs", "#e8f7ff", "#0284c7", "2", "実行中・予定のジョブ"), ("bell", "#fff7e6", "#d97706", "1", "要確認（作りかけ・残り）")]
    kpis = "".join(f'<div class="kpi"><div class="ico" style="background:{bg};color:{fg}">{ic(k,22)}</div><div><div class="v">{v}</div><div class="l">{E(l)}</div></div></div>' for k, bg, fg, v, l in kpi)
    rows = ""
    for i, (sub, rg, app, grp, st, tone, exp, days, dep, job) in enumerate(ROWS):
        pct = 0 if days is None else max(4, min(100, int(days / 90 * 100)))
        col = "#e11d48" if days is not None and days <= 7 else "#10b981"
        name = app or rg
        rows += f'''<tr class="{"sel" if i == 0 else ""}"><td><div class="appcell"><div class="av" style="background:{COLORS[i]}">{E(name[:2].upper())}</div><div><div style="font-weight:600">{E(app) if app else "（アプリなし）"}</div><div style="color:var(--sub);font-size:11.5px">{E(rg)} · {E(sub)}</div></div></div></td>
<td><span class="status s-{tone}">{E(st)}</span>{f'<div style="font-size:11px;color:#3150c9;margin-top:4px">{E(job)}</div>' if job else ""}</td>
<td class="exp">{E(exp)}{f' <span style="color:{col};font-size:11.5px">あと {days} 日</span>' if days is not None else ""}<div class="expbar"><i style="width:{pct}%;background:{col}"></i></div></td>
<td>{f'<span class="pill" style="padding:3px 10px">{E(grp)}</span>' if grp else ""}</td><td style="color:var(--sub)">{E(dep)}</td><td style="color:#9aa3b8">{ic("dots",18)}</td></tr>'''
    body = f'''<div class="kpis">{kpis}</div>
<div class="wrap"><div class="card list"><div class="tools"><span class="pill on">すべて <span class="n">7</span></span><span class="pill">稼働中 <span class="n">3</span></span><span class="pill">期限が近い <span class="n">2</span></span><span class="pill">要確認 <span class="n">2</span></span><span class="pill">{ic("filter",14)}グループ</span>
<span style="margin-left:auto"></span><span class="btn">{ic("refresh",16)}読み直す</span><span class="btn primary">{ic("plus",16)}新しくデプロイ</span></div>
<table><thead><tr><th>アプリ</th><th>状態</th><th>利用可能期限</th><th>グループ</th><th>デプロイ日</th><th></th></tr></thead><tbody>{rows}</tbody></table></div>
<div class="card drawer"><div class="hd"><div class="av" style="background:#6366f1;width:46px;height:46px;font-size:16px">SA</div><div><div style="font-size:17px;font-weight:700">sales</div><div style="color:var(--sub);font-size:12px">rg-sales-demo · 開発用</div></div><span class="status s-ok" style="margin-left:auto">稼働中</span></div>
<div class="sec"><div style="display:flex;gap:8px;flex-wrap:wrap"><span class="btn primary" style="flex:1;justify-content:center">{ic("link",16)}Timetracker を開く</span><span class="btn">{ic("key",16)}</span><span class="btn">{ic("trash",16)}</span></div></div>
<div class="sec"><h4>利用可能期限</h4><div style="display:flex;justify-content:space-between;align-items:baseline"><span style="font-size:20px;font-weight:700;color:#e11d48">あと 3 日</span><span style="color:var(--sub)">11/16 の終わりまで</span></div><div class="expbar"><i style="width:8%;background:#e11d48"></i></div><div style="margin-top:12px;display:flex;gap:8px"><span class="btn" style="height:30px">+7 日</span><span class="btn" style="height:30px">+30 日</span><span class="btn" style="height:30px">日付を選ぶ</span></div></div>
<div class="sec"><h4>Container Apps</h4><div class="apps"><div class="appline"><span>sales-tt</span><span class="status s-ok">Running</span></div><div class="appline"><span>sales-db（PostgreSQL）</span><span class="status s-ok">Running</span></div><div class="appline"><span>sales-redis</span><span class="status s-ok">Running</span></div></div></div>
<div class="sec" style="border:0"><h4>最近のジョブ</h4><div class="timeline"><div class="tl"><span class="d" style="background:#10b981"></span><div><b>デプロイ 完了</b><div style="color:var(--sub)">10/15 10:02 · 6 分 12 秒</div></div></div><div class="tl"><span class="d" style="background:#e5484d"></span><div><b>デプロイ 失敗</b><div style="color:var(--sub)">10/14 18:40 · 権限がありません</div></div><span class="btn" style="height:28px;margin-left:auto;font-size:12px">この設定で</span></div></div></div></div></div>'''
    return page("案 B デプロイの一覧", B_CSS, b_shell(1, "デプロイの一覧", "ホーム / デプロイ", body))
def b_wizard():
    vs = [("対象", "rg-sales-demo / sales", "done"), ("DB", "PostgreSQL", "done"), ("パスワード", "入力済み", "done"), ("スペックと期限", "入力中", "cur"), ("確認と実行", "", "")]
    steps = "".join(f'<div class="vs {c}"><span class="n">{"✓" if c == "done" else i + 1}</span><div><div>{E(t)}</div><div style="font-size:11.5px;font-weight:400;color:var(--sub)">{E(s)}</div></div></div>' for i, (t, s, c) in enumerate(vs))
    body = f'''<div class="wrap"><div class="card vstep"><div style="font-size:12px;color:var(--sub);margin:0 0 8px 12px">5 つの手順</div>{steps}</div>
<div class="card" style="flex:1;display:flex;flex-direction:column"><div style="padding:22px 24px 0"><div style="font-size:20px;font-weight:700">スペックと期限</div><div style="color:var(--sub);margin-top:4px">あとから「期限を変える」で延ばせます。</div></div>
<div class="fgrid"><div class="f" style="grid-column:1/-1"><label>Timetracker の CPU</label><div class="slider"><i></i><b></b></div><div style="display:flex;justify-content:space-between;color:var(--sub);font-size:11.5px"><span>0.25</span><span style="color:var(--accent);font-weight:700">1.5 vCPU / 3 GiB</span><span>4.0</span></div></div>
<div class="f"><label>DB（CPU / メモリ）</label><div class="inp">0.5 vCPU / 1 GiB<span>▾</span></div></div><div class="f"><label>Redis（CPU / メモリ）</label><div class="inp">0.25 vCPU / 0.5 GiB<span>▾</span></div></div>
<div class="f" style="grid-column:1/-1"><label>利用可能期限</label><div class="radio"><div class="ropt"><b>既定</b>今日から 32 日後（12/15）</div><div class="ropt on"><b>日付を指定</b>2026/11/02</div><div class="ropt"><b>期限なし</b>自動で削除しない</div></div>
<div class="inp bad" style="margin-top:12px;width:280px">2026/11/02<span>{ic("clock",16)}</span></div><div style="color:#e5484d;font-size:12px;margin-top:6px">今日以降の日付にしてください（読み込んだジョブの期限が過ぎています）</div></div></div>
<div style="margin-top:auto;padding:16px 24px;border-top:1px solid var(--line);display:flex;justify-content:space-between"><span class="btn">戻る</span><span class="btn primary">確認と実行へ</span></div></div>
<div class="card preview"><div style="padding:20px;border-bottom:1px solid var(--line)"><div style="color:var(--sub);font-size:12px">作るもの</div><div style="font-weight:700;font-size:16px;margin-top:4px">sales（rg-sales-demo）</div></div>
<div style="padding:16px 20px;display:flex;flex-direction:column;gap:10px"><div class="appline"><span>Container Apps Environment</span><span class="status s-info">新規</span></div><div class="appline"><span>sales-tt / -db / -redis</span><span class="status s-info">新規 3</span></div><div class="appline"><span>ストレージアカウント</span><span class="status s-info">新規</span></div></div>
<div style="padding:16px 20px;border-top:1px solid var(--line)"><div style="color:var(--sub);font-size:12px">目安の時間</div><div class="cost">約 8 分</div></div>
<div style="padding:16px 20px;border-top:1px solid var(--line);font-size:12.5px;color:#b45f06;background:#fff8ec;margin:0 16px;border-radius:10px">失敗したジョブ（11/02）の設定を読み込みました。パスワードは入力済みのものを使います。</div></div></div>'''
    return page("案 B 新しくデプロイ", B_CSS, b_shell(2, "新しくデプロイ", "ホーム / デプロイ / 新規", body))

# ---------------- 案 C: コンパクト・ダーク（情報量と操作の速さ） ----------------
C_CSS = """
:root { --bg:#0f1115; --panel:#161a21; --panel2:#1c212a; --line:#272d38; --text:#e6e9ef; --sub:#8b93a5; --accent:#3ea6ff; }
body { background:var(--bg); color:var(--text); display:flex; flex-direction:column; font-size:12.5px; }
.bar1 { height:44px; display:flex; align-items:center; gap:18px; padding:0 16px; border-bottom:1px solid var(--line); background:#12151b; }
.brand { display:flex; align-items:center; gap:8px; font-weight:700; } .brand i { width:16px; height:16px; border-radius:4px; background:linear-gradient(135deg,#3ea6ff,#7c5cff); display:block; }
.tabs { display:flex; gap:2px; height:44px; } .tabs a { display:flex; align-items:center; gap:8px; padding:0 14px; color:var(--sub); border-bottom:2px solid transparent; }
.tabs a.on { color:var(--text); border-bottom-color:var(--accent); }
.cmdk { margin-left:auto; width:320px; height:28px; border:1px solid var(--line); border-radius:6px; background:var(--panel); display:flex; align-items:center; gap:8px; padding:0 10px; color:var(--sub); }
.kbd { margin-left:auto; border:1px solid #333a48; border-radius:4px; padding:0 5px; font-size:10.5px; }
.who { color:var(--sub); }
.bar2 { height:42px; display:flex; align-items:center; gap:6px; padding:0 16px; border-bottom:1px solid var(--line); background:var(--panel); }
.b { height:28px; display:inline-flex; align-items:center; gap:6px; padding:0 10px; border-radius:5px; border:1px solid var(--line); background:var(--panel2); color:var(--text); white-space:nowrap; }
.b.pri { background:var(--accent); border-color:var(--accent); color:#06121f; font-weight:600; } .b.red { color:#ff7b72; }
.fchip { height:26px; display:inline-flex; align-items:center; gap:6px; padding:0 10px; border-radius:13px; border:1px dashed #3a4252; color:var(--sub); }
.fchip.on { border-style:solid; border-color:#2f5d86; background:#13283b; color:#9dd1ff; }
.split { flex:1; display:flex; min-height:0; }
.tbl { flex:1; overflow:hidden; display:flex; flex-direction:column; }
.tbl th { text-align:left; color:var(--sub); font-weight:500; font-size:11.5px; padding:7px 10px; border-bottom:1px solid var(--line); background:#13161c; position:relative; }
.tbl th.sorted::after { content:"▲"; font-size:8px; margin-left:4px; color:var(--accent); }
.tbl td { padding:7px 10px; border-bottom:1px solid #1d222b; white-space:nowrap; }
.tbl tr:nth-child(even) td { background:#12151b; }
.tbl tr.sel td { background:#14304a !important; }
.tbl tr.sel td:first-child { box-shadow: inset 2px 0 0 var(--accent); }
.st { display:inline-flex; align-items:center; gap:6px; } .st i { width:7px; height:7px; border-radius:2px; display:block; }
.ok i { background:#3fb950; } .muted i { background:#6e7681; } .info i { background:#3ea6ff; } .warn i { background:#d29922; }
.red { color:#ff7b72; }
.chk { width:13px; height:13px; border:1px solid #4b5468; border-radius:3px; display:inline-block; vertical-align:middle; } .chk.on { background:var(--accent); border-color:var(--accent); }
.side { width:400px; border-left:1px solid var(--line); background:var(--panel); display:flex; flex-direction:column; }
.side .t { padding:14px 16px; border-bottom:1px solid var(--line); }
.stabs { display:flex; gap:16px; padding:0 16px; border-bottom:1px solid var(--line); } .stabs a { padding:10px 0; color:var(--sub); border-bottom:2px solid transparent; } .stabs a.on { color:var(--text); border-bottom-color:var(--accent); }
.prop { display:grid; grid-template-columns:110px 1fr; gap:7px 10px; padding:14px 16px; }
.prop dt { color:var(--sub); }
.log { margin:0 16px; background:#0c0e12; border:1px solid var(--line); border-radius:6px; padding:10px; font-family: 'Inter', monospace; font-size:11.5px; color:#a8b1c2; line-height:1.6; }
.status { height:26px; display:flex; align-items:center; gap:16px; padding:0 16px; border-top:1px solid var(--line); background:#12151b; color:var(--sub); font-size:11.5px; }
.form { flex:1; padding:18px 24px; overflow:hidden; display:flex; flex-direction:column; gap:14px; }
.sec { border:1px solid var(--line); border-radius:8px; background:var(--panel); }
.sec .h { display:flex; align-items:center; gap:10px; padding:10px 14px; border-bottom:1px solid var(--line); font-weight:600; }
.sec .h .n { width:20px; height:20px; border-radius:5px; background:#233; display:grid; place-items:center; font-size:11px; color:#9dd1ff; background:#13283b; }
.sec .h .ok { margin-left:auto; color:#3fb950; font-weight:500; font-size:11.5px; }
.rowf { display:grid; grid-template-columns: repeat(4, 1fr); gap:12px; padding:12px 14px; }
.fl label { display:block; color:var(--sub); font-size:11.5px; margin-bottom:5px; }
.in { height:30px; border:1px solid var(--line); border-radius:5px; background:#0f1217; display:flex; align-items:center; justify-content:space-between; padding:0 9px; }
.in.bad { border-color:#f85149; }
.collapsed { color:var(--sub); padding:10px 14px; }
.plan { font-family:'Inter',monospace; font-size:11.5px; line-height:1.8; padding:12px 14px; }
.plan .add { color:#3fb950; } .plan .chg { color:#d29922; }
"""
def c_shell(active, toolbar, main, status):
    tabs = "".join(f'<a class="{"on" if i == active else ""}">{ic(k,15)}{E(t)}</a>' for i, (k, t) in enumerate(NAV[1:4], start=1))
    return f'''<div class="bar1"><div class="brand"><i></i>TTDeployTool</div><div class="tabs">{tabs}<a>{ic("flask",15)}ローカル検証</a><a>{ic("gear",15)}設定</a></div>
<div class="cmdk">{ic("search",14)}コマンド・デプロイを探す<span class="kbd">Ctrl K</span></div><span class="who">mock-user@example.com ▾</span></div>
<div class="bar2">{toolbar}</div><div class="split">{main}</div><div class="status">{status}</div>'''
def c_list():
    rows = ""
    for i, (sub, rg, app, grp, st, tone, exp, days, dep, job) in enumerate(ROWS):
        soon = days is not None and days <= 3
        rows += f'''<tr class="{"sel" if i == 0 else ""}"><td><span class="chk {"on" if i == 0 else ""}"></span></td><td style="font-weight:600">{E(app) or '<span style="color:var(--sub)">—</span>'}</td><td>{E(rg)}</td><td style="color:var(--sub)">{E(sub)}</td><td>{E(grp)}</td>
<td><span class="st {tone}"><i></i>{E(st)}</span></td><td class="{"red" if soon else ""}">{E(exp)}{f"（{days}日）" if days is not None and days <= 7 else ""}</td><td style="color:var(--sub)">{E(dep)}</td><td style="color:#9dd1ff">{E(job)}</td></tr>'''
    rows += "".join(f'<tr><td><span class="chk"></span></td><td style="font-weight:600">app{n:02d}</td><td>rg-mock-{n:02d}</td><td style="color:var(--sub)">検証用</td><td></td><td><span class="st ok"><i></i>稼働中</span></td><td>12/{n:02d}</td><td style="color:var(--sub)">10/{n:02d}</td><td></td></tr>' for n in range(10, 24))
    tb = f'''<span class="b pri">{ic("plus",14)}新しくデプロイ</span><span class="b">{ic("refresh",14)}読み直す</span><span class="b">{ic("clock",14)}期限</span><span class="b red">{ic("trash",14)}削除…</span>
<span style="width:1px;height:18px;background:var(--line);margin:0 6px"></span><span class="fchip on">サブスクリプション: すべて ×</span><span class="fchip on">並び: アプリ名 ×</span><span class="fchip">+ 条件を足す</span><span style="margin-left:auto;color:var(--sub)">1 件を選択</span>'''
    main = f'''<div class="tbl"><table><thead><tr><th style="width:30px"><span class="chk"></span></th><th class="sorted">アプリ名</th><th>リソースグループ</th><th>サブスクリプション</th><th>グループ</th><th>状態</th><th>期限</th><th>デプロイ日</th><th>ジョブ</th></tr></thead><tbody>{rows}</tbody></table></div>
<aside class="side"><div class="t"><div style="color:var(--sub);font-size:11.5px">rg-sales-demo · 開発用</div><div style="font-size:16px;font-weight:700;margin-top:2px">sales <span class="st ok" style="font-size:12px;font-weight:400;margin-left:8px"><i></i>稼働中</span></div>
<div style="display:flex;gap:6px;margin-top:10px"><span class="b pri">{ic("link",14)}開く</span><span class="b">{ic("key",14)}パスワード</span><span class="b">{ic("refresh",14)}この設定で</span><span class="b">{ic("dots",14)}</span></div></div>
<div class="stabs"><a class="on">概要</a><a>Azure の状態</a><a>ジョブ（3）</a><a>タグ</a></div>
<dl class="prop"><dt>URL</dt><dd style="color:var(--accent)">https://sales-tt.japaneast…</dd><dt>期限</dt><dd class="red">11/16 の終わり（あと 3 日）</dd><dt>DB</dt><dd>PostgreSQL · Azure Files</dd><dt>Timetracker</dt><dd>1.5 vCPU / 3 GiB · 2.6.1</dd><dt>DB / Redis</dt><dd>0.5 / 1 GiB · 0.25 / 0.5 GiB</dd><dt>グループ</dt><dd>営業</dd><dt>デプロイ日</dt><dd>2026-10-15 10:02</dd></dl>
<div style="padding:0 16px 8px;color:var(--sub)">最近のログ</div><div class="log">10:02:11 デプロイが終わりました（6 分 12 秒）<br>10:01:40 Timetracker の起動を確かめています<br>09:58:02 Container App を作成: sales-tt<br>09:57:30 Container App を作成: sales-db</div></aside>'''
    st = '<span>● サインイン済み</span><span>ジョブ: 実行中 1 · 予定 1</span><span>次の期限の確認 16:00</span><span style="margin-left:auto">Alt+Shift+←/→ 列の移動 · Alt+←/→ 列の幅</span>'
    return page("案 C デプロイの一覧", C_CSS, c_shell(1, tb, main, st))
def c_wizard():
    tb = f'''<span style="font-weight:600">新しくデプロイ</span><span style="color:var(--sub)">— 1 つの画面で入力し、右で計画を確かめてから実行します</span><span style="margin-left:auto"></span><span class="b">テンプレート ▾</span><span class="b">Markdown でコピー</span><span class="b">初期値に戻す</span>'''
    main = f'''<div class="form">
<div class="sec"><div class="h"><span class="n">1</span>対象<span class="ok">✓ 開発用 / rg-sales-demo / sales / japaneast</span></div></div>
<div class="sec"><div class="h"><span class="n">2</span>DB<span class="ok">✓ PostgreSQL · timetracker · 永続化する</span></div></div>
<div class="sec"><div class="h"><span class="n">3</span>パスワード<span class="ok">✓ 2 つとも入力済み</span></div></div>
<div class="sec"><div class="h"><span class="n">4</span>スペックと期限<span style="margin-left:auto;color:#f85149;font-weight:500;font-size:11.5px">1 件の誤り</span></div>
<div class="rowf"><div class="fl"><label>TT CPU</label><div class="in">1.5<span style="color:var(--sub)">vCPU</span></div></div><div class="fl"><label>TT メモリ</label><div class="in">3.0<span style="color:var(--sub)">GiB</span></div></div><div class="fl"><label>DB CPU / メモリ</label><div class="in">0.5 / 1.0</div></div><div class="fl"><label>Redis CPU / メモリ</label><div class="in">0.25 / 0.5</div></div>
<div class="fl"><label>期限</label><div class="in">日付を指定 ▾</div></div><div class="fl"><label>期限日</label><div class="in bad">2026-11-02<span>{ic("clock",14)}</span></div></div><div class="fl" style="grid-column:span 2"><label>グループ</label><div class="in">営業 ▾</div></div></div>
<div style="padding:0 14px 12px;color:#ff7b72">利用可能期限日（2026-11-02）は、今日以降の日付を指定してください。</div></div>
<div class="sec"><div class="h"><span class="n">5</span>予定</div><div class="rowf"><div class="fl"><label>実行</label><div class="in">今すぐ ▾</div></div><div class="fl"><label>予定の日付</label><div class="in">2026-11-13</div></div><div class="fl"><label>時刻</label><div class="in">15:43</div></div><div class="fl"><label>終了しても残す</label><div class="in">はい ▾</div></div></div></div>
</div>
<aside class="side"><div class="t"><div style="font-weight:700">計画（Azure は読むだけ）</div><div style="color:var(--sub);margin-top:2px">入力を変えると作り直します · 有効 15 分</div></div>
<div class="plan"><div class="add">+ リソースグループ rg-sales-demo</div><div class="add">+ Container Apps Environment sales-env</div><div class="add">+ ストレージアカウント stsales…</div><div class="add">+ Container App sales-db</div><div class="add">+ Container App sales-redis</div><div class="add">+ Container App sales-tt</div><div class="chg">~ タグ: 期限・グループ・デプロイ日</div></div>
<div style="padding:12px 16px;border-top:1px solid var(--line);color:var(--sub)">目安 約 8 分 · 権限の確認 ✓</div>
<div style="margin-top:auto;padding:14px 16px;border-top:1px solid var(--line);display:flex;gap:8px"><span class="b pri" style="opacity:.45">今すぐ実行</span><span class="b" style="opacity:.45">予定に追加</span><span style="margin-left:auto;color:#ff7b72;align-self:center">4 の誤りを直してください</span></div></aside>'''
    st = '<span>● サインイン済み</span><span>失敗したジョブ（11/02 10:50）の設定を読み込みました · パスワードは入力済みのものを使います</span>'
    return page("案 C 新しくデプロイ", C_CSS, c_shell(2, tb, main, st))


# ---------------- 推奨: 案 A を基本に、案 B の件数のカードと期限の残りのバー ----------------
D_CSS = A_CSS + """
.kpis { display:grid; grid-template-columns: repeat(4, 1fr); gap:12px; }
.kpi { display:flex; gap:12px; align-items:center; padding:12px 14px; cursor:default; }
.kpi .ico { width:36px; height:36px; border-radius:8px; display:grid; place-items:center; }
.kpi .v { font-size:22px; font-weight:600; line-height:1.1; } .kpi .l { color:var(--sub); font-size:12px; }
.kpi.on { border-color:var(--accent); box-shadow:0 0 0 1px var(--accent) inset; }
.kpi .go { margin-left:auto; color:var(--accent); font-size:12px; }
.expcell { display:flex; flex-direction:column; gap:5px; }
.expbar { height:4px; border-radius:2px; background:#e6e6e6; overflow:hidden; width:110px; } .expbar i { display:block; height:100%; border-radius:2px; }
.big { font-size:20px; font-weight:600; }
.quick { display:flex; gap:6px; margin-top:8px; } .quick .btn { height:28px; padding:0 10px; font-size:12px; }
.jstate { display:inline-flex; align-items:center; gap:6px; font-size:12px; padding:2px 8px; border-radius:10px; }
.j-ok { background:#dff6dd; color:#0f5c0f; } .j-fail { background:#fde7e9; color:#a4262c; } .j-run { background:#e6f0fa; color:#004c8f; } .j-sch { background:#f3eefb; color:#5c2e91; } .j-cancel { background:#eeeeee; color:#555; }
.progress { height:4px; background:#e6e6e6; border-radius:2px; width:120px; overflow:hidden; margin-top:5px; } .progress i { display:block; height:100%; background:var(--accent); }
"""
def d_kpis(items):
    return '<div class="kpis">' + "".join(
        f'<div class="surface kpi {"on" if on else ""}"><div class="ico" style="background:{bg};color:{fg}">{ic(k,20)}</div><div><div class="v">{v}</div><div class="l">{E(l)}</div></div>{f"<span class=go>{E(go)}</span>" if go else ""}</div>'
        for k, bg, fg, v, l, go, on in items) + '</div>'
def d_list():
    kpis = d_kpis([("cloud", "#e6f0fa", "#005fb8", "7", "デプロイ（2 サブスクリプション）", "", True), ("clock", "#fde7e9", "#c42b1c", "2", "期限が 7 日以内", "絞り込む ›", False),
                   ("jobs", "#e6f0fa", "#005fb8", "2", "実行中・予定のジョブ", "ジョブの一覧 ›", False), ("bell", "#fff4ce", "#9d5d00", "2", "要確認（作りかけなど）", "絞り込む ›", False)])
    rows = ""
    for i, (sub, rg, app, grp, st, tone, exp, days, dep, job) in enumerate(ROWS):
        sel = i == 0
        if days is None:
            exp_html = f'<span>{E(exp)}</span>'
        else:
            col = "#c42b1c" if days <= 7 else "#0f7b0f" if days > 30 else "#c27c00"
            pct = max(4, min(100, int(days / 90 * 100)))
            exp_html = f'<div class="expcell"><span><span style="{"color:#c42b1c;font-weight:600" if days <= 7 else ""}">{E(exp)}</span> <span class="sub" style="display:inline">あと {days} 日</span></span><div class="expbar"><i style="width:{pct}%;background:{col}"></i></div></div>'
        rows += f"""<tr class="{"sel" if sel else ""}"><td><span class="cb {"on" if sel else ""}"></span></td>
<td>{E(app) if app else '<span class="sub" style="display:inline">—</span>'}</td><td>{E(rg)}</td><td>{E(sub)}</td>
<td>{f'<span class="chip">{E(grp)}</span>' if grp else ""}</td><td><span class="badge {tone}"><span class="dot"></span>{E(st)}</span></td>
<td>{exp_html}</td><td>{f'<span class="jobtag">{E(job)}</span>' if job else ""}</td></tr>"""
    main = f"""<div class="head"><div><h1>デプロイの一覧</h1><div class="sub">本ツールでデプロイした環境。最終更新 11/13 15:42</div></div></div>
{kpis}
<div class="surface cmd"><span class="btn primary">{ic("plus",16)}新しくデプロイ</span><span class="sep"></span>
<span class="btn ghost">{ic("refresh",16)}読み直す</span><span class="btn ghost">{ic("clock",16)}期限を変える</span><span class="btn ghost danger">{ic("trash",16)}Azure から削除…</span><span class="btn ghost">{ic("filter",16)}絞り込み</span>
<div class="search">{ic("search",16)}アプリ名・グループで探す</div></div>
<div class="content"><div class="surface grid"><table><thead><tr><th style="width:40px"><span class="cb"></span></th><th style="width:84px">アプリ名</th><th style="width:124px">リソースグループ</th><th style="width:96px">サブスクリプション</th><th style="width:86px">グループ</th><th style="width:150px">状態</th><th style="width:150px">利用可能期限</th><th>ジョブ</th></tr></thead><tbody>{rows}</tbody></table>
<div class="pager"><span>1〜7 件目（全 7 件）</span><span>20 件ずつ ▾</span></div></div>
<aside class="surface detail"><div><div class="sub" style="margin:0 0 4px">rg-sales-demo · 開発用</div><h2>sales <span class="badge ok" style="font-size:12px;font-weight:400;margin-left:6px"><span class="dot"></span>稼働中（3/3）</span></h2></div>
<div><div class="sub" style="margin:0 0 6px">利用可能期限</div><div style="display:flex;justify-content:space-between;align-items:baseline"><span class="big" style="color:#c42b1c">あと 3 日</span><span class="sub" style="margin:0">11/16 の終わりまで</span></div>
<div class="expbar" style="width:100%;height:6px;margin-top:8px"><i style="width:8%;background:#c42b1c"></i></div>
<div class="quick"><span class="btn">+7 日</span><span class="btn">+30 日</span><span class="btn">{ic("clock",14)}日付を選ぶ</span></div><div class="hint">期限を過ぎると自動で削除します</div></div>
<dl class="kv"><dt>URL</dt><dd style="color:var(--accent)">sales-tt.japaneast.…</dd><dt>DB</dt><dd>PostgreSQL・永続化</dd><dt>スペック</dt><dd>TT 1.5 CPU / 3 GiB</dd><dt>グループ</dt><dd><span class="chip">営業</span></dd><dt>デプロイ日</dt><dd>2026/10/15</dd></dl>
<span class="btn primary" style="justify-content:center">{ic("link",16)}Timetracker を開く</span>
<div style="display:flex;flex-wrap:wrap;gap:8px"><span class="btn">{ic("key",16)}パスワード</span><span class="btn">{ic("cloud",16)}状態を確認</span><span class="btn">{ic("refresh",16)}この設定で新しくデプロイ</span></div></aside></div>"""
    return page("推奨 デプロイの一覧", D_CSS, a_shell(1, main))
def d_wizard():
    html_ = a_wizard().replace("案 A 新しくデプロイ", "推奨 新しくデプロイ")
    # 期限の欄の下に、期限の残りのバー（入れた日付までの日数）を見せる
    html_ = html_.replace('<div class="field"><label>グループ</label>', '<div class="field" style="grid-column:1/-1;margin-top:-6px"><div style="display:flex;align-items:center;gap:10px"><div class="expbar" style="width:220px;height:6px"><i style="width:0%"></i></div><span style="color:#c42b1c;font-size:12px">期限まで: 過ぎています（12/15 にすると あと 32 日）</span></div></div><div class="field"><label>グループ</label>', 1)
    return html_.replace("<style>" + BASE + A_CSS, "<style>" + BASE + D_CSS)
def d_jobs():
    kpis = d_kpis([("jobs", "#e6f0fa", "#005fb8", "1", "実行中", "", False), ("clock", "#f3eefb", "#5c2e91", "1", "予定", "", False),
                   ("bell", "#fde7e9", "#c42b1c", "1", "失敗（7 日以内）", "絞り込む ›", True), ("check", "#dff6dd", "#0f7b0f", "12", "完了（7 日以内）", "", False)])
    J = [("デプロイ", "rg-qa-02 / qa02", "j-run", "実行中", "62%", "11/13 15:30", "Container App を作成: qa02-tt"),
         ("削除", "rg-poc-ai / poc", "j-sch", "予定 11/14 18:00", "", "11/13 09:12", "リソースグループも削除する"),
         ("デプロイ", "tt-test-1 / timetracker", "j-fail", "失敗", "", "11/02 09:43", "利用可能期限日（2026-11-02）は、今日以降の日付を指定してください。"),
         ("期限での削除", "tt-test-2 / timetracker", "j-ok", "完了", "", "11/02 00:00", "4 件を削除しました"),
         ("デプロイ", "rg-review / review", "j-ok", "完了", "", "11/05 10:20", "https://review-tt.japaneast.…"),
         ("削除", "tt-test-x / timetracker", "j-cancel", "取り消し", "", "11/02 09:40", "")]
    rows = ""
    for i, (k, t, cls, st, pr, reg, res) in enumerate(J):
        sel = i == 2
        rows += f"""<tr class="{"sel" if sel else ""}"><td><span class="cb {"on" if sel else ""}"></span></td><td>{E(k)}</td><td>{E(t)}</td>
<td><span class="jstate {cls}">{E(st)}</span>{f'<div class="progress"><i style="width:{pr}"></i></div>' if pr else ""}</td><td class="mono">{E(reg)}</td><td style="color:{"#a4262c" if cls == "j-fail" else "var(--sub)"}">{E(res)}</td></tr>"""
    main = f"""<div class="head"><div><h1>ジョブの一覧</h1><div class="sub">このアプリで登録したデプロイ・削除。終わったジョブは 180 日・500 件まで記録に残します</div></div></div>
{kpis}
<div class="surface cmd"><span class="btn">{ic("jobs",16)}詳細</span><span class="btn primary">{ic("refresh",16)}この設定で新しくデプロイ</span><span class="sep"></span><span class="btn ghost">取り消し</span><span class="btn ghost">一時停止</span><span class="btn ghost">再開</span><span class="btn ghost">{ic("clock",16)}予定を変える</span><span class="btn ghost danger">{ic("trash",16)}記録から削除</span>
<div class="search">{ic("search",16)}対象・結果で探す</div></div>
<div class="content"><div class="surface grid"><table><thead><tr><th style="width:40px"><span class="cb"></span></th><th style="width:110px">種類</th><th style="width:200px">対象</th><th style="width:160px">状態</th><th style="width:110px">登録日時</th><th>結果</th></tr></thead><tbody>{rows}</tbody></table>
<div class="pager"><span>1〜6 件目（全 6 件）</span><span>20 件ずつ ▾</span></div></div>
<aside class="surface detail"><div><div class="sub" style="margin:0 0 4px">デプロイ · 開発用</div><h2>tt-test-1 / timetracker</h2></div>
<div class="info-bar" style="background:#fde7e9;border-color:#f4c7cb">{ic("bell",18)}<div><b>失敗</b>: 実行の直前の確認で、利用可能期限日（2026-11-02）が過ぎていました。</div></div>
<div class="info-bar">{ic("clock",18)}<div>予定（11/02 10:50）より 2263 分遅れて実行しました（PC のスリープなど）。</div></div>
<dl class="kv"><dt>登録</dt><dd>11/02 09:43</dd><dt>予定の変更</dt><dd>11/02 09:44</dd><dt>実行</dt><dd>11/04 00:33（3 秒）</dd></dl>
<span class="btn primary" style="justify-content:center">{ic("refresh",16)}この設定で新しくデプロイ</span><div class="hint" style="margin-top:-6px">期限を直して出し直せます。パスワードは入れ直してください</div>
<div style="display:flex;gap:8px"><span class="btn">Markdown でコピー</span><span class="btn">ログを見る</span></div></aside></div>"""
    return page("推奨 ジョブの一覧", D_CSS, a_shell(3, main))

for name, fn in [("A-list", a_list), ("A-wizard", a_wizard), ("B-list", b_list), ("B-wizard", b_wizard), ("C-list", c_list), ("C-wizard", c_wizard), ("D-list", d_list), ("D-wizard", d_wizard), ("D-jobs", d_jobs)]:
    open(f"{name}.html", "w", encoding="utf-8").write(fn())
print("ok")
