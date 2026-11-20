const { test, expect } = require('@playwright/test');

test('live media shows LIVE instead of an empty seek bar and restores the timeline for recordings', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(async () => {
    const main = await import('/panel/main.js');
    const media = { app: 'Edge', title: 'Live broadcast', playing: true, durMs: 0, posMs: 0, canSeek: true, canNext: false, canPrevious: false };
    main.D.state = { ...main.D.state, media };
    main.setMediaState(media);
    main.emit('state');
  });
  const widget = page.locator('.w-media').first();
  await expect(widget.locator('.bar')).toBeHidden();
  await expect(widget.locator('.m-dur')).toHaveText('LIVE');
  await expect(widget.locator('.m-pos')).toBeHidden();
  await expect(widget.locator('[data-media="next"]')).toBeDisabled();
  await expect(widget.locator('[data-media="prev"]')).toBeDisabled();
  await page.evaluate(async () => {
    const main = await import('/panel/main.js');
    const media = { app: 'Edge', title: 'Recorded video', playing: false, durMs: 180000, posMs: 60000, canSeek: true };
    main.D.state = { ...main.D.state, media };
    main.setMediaState(media);
    main.emit('state');
  });
  await expect(widget.locator('.bar')).toBeVisible();
  await expect(widget.locator('.m-dur')).toHaveText('3:00');
  await expect(widget.locator('[data-media="next"]')).toBeEnabled();
});
