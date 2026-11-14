// アプリのアイコン（オーナーの用意した SVG）から、MSIX の画像を作る（2026-11-14）。node docs/design/desktop/icon/render.js <出力のフォルダー>
// 48 ピクセル以下は、線を太くした小さい版（TTDeployTool_icon_small.svg）を使う。横長の画像は、真ん中にアイコンを置き、まわりは透明
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');
const out = process.argv[2];
const big = fs.readFileSync(path.join(__dirname, 'TTDeployTool_icon.svg')).toString('base64');
const small = fs.readFileSync(path.join(__dirname, 'TTDeployTool_icon_small.svg')).toString('base64');
const images = [
  ['Square44x44Logo.png', 44, 44, 44, small],
  ['StoreLogo.png', 50, 50, 50, small],
  ['Square150x150Logo.png', 150, 150, 150, big],
  ['Wide310x150Logo.png', 310, 150, 150, big],
  ['SplashScreen.png', 620, 300, 300, big],
];
(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  for (const [name, w, h, size, svg] of images) {
    await page.setViewportSize({ width: w, height: h });
    await page.setContent(`<html><body style="margin:0;background:transparent;display:flex;align-items:center;justify-content:center;width:${w}px;height:${h}px">` +
      `<img src="data:image/svg+xml;base64,${svg}" width="${size}" height="${size}"></body></html>`);
    await page.waitForFunction(() => document.images[0].complete);
    await page.screenshot({ path: path.join(out, name), omitBackground: true });
  }
  await browser.close();
})();
