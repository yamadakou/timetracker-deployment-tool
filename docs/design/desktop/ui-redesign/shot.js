// 画面イメージを PNG にする（node shot.js）。Chromium は環境の Playwright のもの
const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
  for (const name of ['A-list', 'A-wizard', 'B-list', 'B-wizard', 'C-list', 'C-wizard']) {
    await page.goto('file://' + __dirname + '/' + name + '.html');
    await page.waitForTimeout(300);
    await page.screenshot({ path: __dirname + '/' + name + '.png' });
  }
  await browser.close();
})();
