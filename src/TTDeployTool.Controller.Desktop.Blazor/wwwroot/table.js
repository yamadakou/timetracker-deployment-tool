// 表の画面の側の小さな JavaScript（docs/design/desktop/desktop-blazor-hybrid.md の 9）。判断は .NET（Core・HtmlTableRules）が行い、ここはブラウザーの入力を渡すだけにする。

const minWidth = 60;

// 見出しの右端のつまみでの幅の変更・見出しのボタンでの Alt+←/→（幅）と Alt+Shift+←/→（順序）・6 行で切れたセルのツールチップ
export function attach(root, dotnet) {
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
    if (!cell) return;
    if (cell.scrollHeight > cell.clientHeight + 1) cell.title = cell.textContent;
    else cell.removeAttribute('title');
  });
  sync(root);
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
