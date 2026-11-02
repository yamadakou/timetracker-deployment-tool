// 表の画面の側の小さな JavaScript（docs/design/desktop/desktop-blazor-hybrid.md の 9）。判断は .NET（Core・HtmlTableRules）が行い、ここはブラウザーの入力を渡すだけにする。

const minWidth = 60;

const dragThreshold = 6;

// 見出しの右端のつまみでの幅の変更・見出しのドラッグでの順序の変更・見出しのボタンでの Alt+←/→（幅）と Alt+Shift+←/→（順序）・6 行で切れたセルのツールチップ
export function attach(root, dotnet) {
  root.addEventListener('pointerdown', e => startColumnDrag(root, dotnet, e));
  root.addEventListener('pointerdown', e => {
    const grip = e.target.closest('.tt-grip');
    if (!grip) return;
    // 見出しのドラッグ（順序の変更）を始めない
    e.preventDefault();
    e.stopPropagation();
    const key = grip.dataset.key;
    const col = root.querySelector(`col[data-key="${CSS.escape(key)}"]`);
    const start = e.clientX;
    const startWidth = grip.closest('th').getBoundingClientRect().width;
    // 動かしている間だけ幅を直接変える。終わったら元に戻し、.NET が描き直す（Blazor が知らない値を DOM に残さない）
    const original = col.getAttribute('style');
    let width = startWidth;
    grip.setPointerCapture(e.pointerId);
    const move = ev => {
      width = Math.max(minWidth, startWidth + ev.clientX - start);
      col.style.width = `${width}px`;
    };
    const up = () => {
      grip.removeEventListener('pointermove', move);
      grip.removeEventListener('pointerup', up);
      if (original === null) col.removeAttribute('style'); else col.setAttribute('style', original);
      dotnet.invokeMethodAsync('ColumnResized', key, width);
    };
    grip.addEventListener('pointermove', move);
    grip.addEventListener('pointerup', up);
  });
  root.addEventListener('keydown', e => {
    const head = e.target.closest('[data-head-key]');
    if (!head || !e.altKey || (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight')) return;
    // Alt+← はブラウザーの「戻る」のため、止める
    e.preventDefault();
    const step = e.key === 'ArrowLeft' ? -1 : 1;
    dotnet.invokeMethodAsync(e.shiftKey ? 'ColumnShifted' : 'ColumnNudged', head.dataset.headKey, step);
  });
  root.addEventListener('mouseover', e => {
    const cell = e.target.closest('.tt-cell');
    // 行の ViewModel がツールチップを持つセル（グループの食い違い・各アプリの状態）は、そのまま出す
    if (!cell || cell.hasAttribute('data-tip')) return;
    if (cell.scrollHeight > cell.clientHeight + 1) cell.title = cell.textContent;
    else cell.removeAttribute('title');
  });
  sync(root);
}

// 見出しのドラッグで列の順序を変える（ポインターのイベント。ブラウザーの標準のドラッグは、WPF のホストの WebView2 では働かなかった）。
// 少し動かしてからドラッグとみなし（ただの押下は並べ替えのボタンのまま）、入る位置を線で示す。離した所の見出しの列の位置に動かす
function startColumnDrag(root, dotnet, e) {
  const head = e.target.closest('.tt-head');
  if (!head || e.button !== 0 || e.target.closest('.tt-filter')) return;
  const source = head.closest('th');
  const key = head.dataset.dragKey;
  const startX = e.clientX;
  const startY = e.clientY;
  let dragging = false;
  let target = null;
  const headers = () => [...root.querySelectorAll('thead th[data-key]')];
  const mark = th => {
    for (const h of headers()) h.classList.remove('tt-drop-before', 'tt-drop-after');
    target = th && th !== source ? th : null;
    if (target) {
      const list = headers();
      target.classList.add(list.indexOf(target) < list.indexOf(source) ? 'tt-drop-before' : 'tt-drop-after');
    }
  };
  const move = ev => {
    if (!dragging) {
      if (Math.abs(ev.clientX - startX) < dragThreshold && Math.abs(ev.clientY - startY) < dragThreshold) return;
      dragging = true;
      source.classList.add('tt-dragging');
      head.setPointerCapture(ev.pointerId);
    }
    const under = document.elementFromPoint(ev.clientX, ev.clientY)?.closest('thead th[data-key]');
    mark(under && root.contains(under) ? under : null);
  };
  const end = ev => {
    window.removeEventListener('pointermove', move);
    window.removeEventListener('pointerup', end);
    window.removeEventListener('pointercancel', end);
    if (!dragging) return;
    const moved = ev.type === 'pointerup' ? target : null;
    mark(null);
    source.classList.remove('tt-dragging');
    // ドラッグの後の click で、並べ替えのボタンを押したことにしない（click が起きなければ、次の本物の click を捨てないよう、すぐ外す）
    const swallow = c => { c.stopPropagation(); c.preventDefault(); };
    window.addEventListener('click', swallow, { capture: true, once: true });
    setTimeout(() => window.removeEventListener('click', swallow, { capture: true }), 0);
    if (moved) dotnet.invokeMethodAsync('ColumnMoved', key, moved.dataset.key);
  };
  window.addEventListener('pointermove', move);
  window.addEventListener('pointerup', end);
  window.addEventListener('pointercancel', end);
}

// 列を動かした後、その列の見出しにフォーカスを戻す（見出しのボタンか、並べ替えない列の見出しの文字）
export function focusHead(root, key) {
  root.querySelector(`[data-head-key="${CSS.escape(key)}"]`)?.focus();
}

// 左上のチェックボックスの「一部を選んだ」（HTML の属性では出せない）
export function sync(root) {
  for (const box of root.querySelectorAll('input[data-indeterminate]')) {
    box.indeterminate = box.dataset.indeterminate === 'true';
  }
}

// 日本語などの変換の始まりと終わり（Core の TextFilterInput が、変換の間は絞り込まない）
export function attachComposition(input, dotnet) {
  input.addEventListener('compositionstart', () => dotnet.invokeMethodAsync('CompositionStarted'));
  input.addEventListener('compositionend', () => dotnet.invokeMethodAsync('CompositionEnded'));
}

// Core の文字が外から変わったときだけ、入力欄に入れる
export function setValue(input, value) {
  if (input.value !== value) input.value = value;
}
