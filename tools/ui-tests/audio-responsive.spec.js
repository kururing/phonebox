const { test, expect } = require('@playwright/test');

for (const layout of ['horizontal', 'vertical']) {
test(`audio faders fill their widgets and keep controls aligned in ${layout} layout`, async ({ page }) => {
  await page.addInitScript(layout => {
    const config = JSON.stringify({ night: { enabled: false }, display: { idleReturnSec: 0 },
      pages: [{ id: 'audio', title: 'Audio', widgets: [
        { id: 'mixer', type: 'audio-mixer', x: 0, y: 0, w: 10, h: 6, options: { layout } },
        { id: 'mic', type: 'microphone', x: 10, y: 0, w: 2, h: 6 },
      ] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  }, layout);
  await page.goto('/panel/');
  const mic = page.locator('.w-microphone');
  const mixer = page.locator('.w-audio-mixer');
  await expect(mic.getByRole('slider')).toHaveCount(1);
  await expect(mic.locator('.deck-button svg')).toHaveCount(1);
  await expect(mic.locator('.deck-button')).toHaveAttribute('aria-pressed', 'true');
  await mic.locator('.deck-button').click();
  await expect(mic.locator('.deck-button')).toHaveAttribute('aria-pressed', 'false');
  await expect(mic.locator('.deck-button svg path[d="M3 3 21 21"]')).toHaveCount(0);
  await expect(mic.getByRole('slider')).toHaveCSS('writing-mode', 'vertical-lr');
  await expect(mixer.getByRole('slider').first()).toHaveCSS('writing-mode', layout === 'vertical' ? 'vertical-lr' : 'horizontal-tb');
  const geometry = await mixer.locator('.deck-channel').first().evaluate(e => {
    const button = e.querySelector('.deck-mute').getBoundingClientRect();
    const icon = e.querySelector('.deck-mute svg').getBoundingClientRect();
    const meter = e.querySelector('.deck-meter').getBoundingClientRect();
    const slider = e.querySelector('.deck-slider').getBoundingClientRect();
    return { iconOffset: Math.abs(icon.x + icon.width / 2 - button.x - button.width / 2),
      meterOffset: Math.abs(meter.x - slider.x), widthOffset: Math.abs(meter.width - slider.width) };
  });
  expect(geometry.iconOffset).toBeLessThan(1);
  if (layout === 'horizontal') {
    expect(geometry.meterOffset).toBeLessThan(1);
    expect(geometry.widthOffset).toBeLessThan(1);
  } else {
    expect(await mixer.getByRole('slider').first().evaluate(e => e.getBoundingClientRect().height)).toBeGreaterThan(120);
    const meterSize = await mixer.locator('.deck-meter').first().boundingBox();
    expect(meterSize.height).toBeGreaterThan(meterSize.width * 5);
  }
  const micMeter = await mic.locator('.deck-meter').boundingBox();
  expect(micMeter.height).toBeGreaterThan(micMeter.width * 5);
  for (const widget of layout === 'vertical' ? [mic, mixer] : [mic]) {
    const centers = await widget.locator('.deck-channel').first().evaluate(e => {
      const center = selector => { const r = e.querySelector(selector).getBoundingClientRect(); return r.x + r.width / 2; };
      return [(center('.deck-slider') + center('.deck-meter')) / 2, center('.deck-value'), center('.deck-mute')];
    });
    expect(Math.max(...centers) - Math.min(...centers)).toBeLessThan(1);
  }
  expect(await mic.locator('.deck-list').evaluate(e => e.scrollWidth <= e.clientWidth)).toBe(true);
  const rowSize = await mic.locator('.deck-volume-row').evaluate(e => ({width:e.clientWidth,scroll:e.scrollWidth}));
  expect(rowSize.scroll).toBeLessThanOrEqual(rowSize.width);
  const height = await mic.locator('.deck-list').evaluate(e => ({ available: e.clientHeight, channel: e.firstElementChild.getBoundingClientRect().height }));
  expect(height.channel).toBeGreaterThanOrEqual(height.available - 2);
  await mic.getByRole('slider').focus();
  const previous = Number(await mic.getByRole('slider').inputValue());
  await mic.getByRole('slider').press('ArrowDown');
  await expect(mic.getByRole('slider')).toHaveValue(String(previous - 1));
  // Resizing the same widget must switch back without rebuilding its controls.
  await mic.evaluate(e => { e.style.width = '300px'; });
  await expect(mic.getByRole('slider')).toHaveCSS('writing-mode', 'horizontal-tb');
  if (layout === 'vertical') {
    await mixer.evaluate(e => { e.style.width = '160px'; });
    const bank = await mixer.locator('.deck-list').evaluate(e => ({
      scroll: e.scrollWidth > e.clientWidth,
      tops: Array.from(e.children).map(c => c.getBoundingClientRect().top),
    }));
    expect(bank.scroll).toBe(true);
    expect(Math.max(...bank.tops) - Math.min(...bank.tops)).toBeLessThan(1);
  }
});
}
