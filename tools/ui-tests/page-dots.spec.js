const { test, expect } = require('@playwright/test');

test('page dots appear while navigating and hide again while idle', async ({ page }) => {
  await page.goto('/panel/');
  const dots = page.locator('#dots');
  await expect(dots.locator('i').first()).toBeAttached();
  await expect(dots).toHaveCSS('opacity', '0', { timeout: 5000 });
  await page.evaluate(async () => { (await import('/panel/main.js')).goPage(1, true); });
  await expect(dots).toHaveCSS('opacity', '1');
  await expect(dots.locator('i').nth(1)).toHaveClass('on');
  await expect(dots).toHaveCSS('opacity', '0', { timeout: 5000 });
  await expect(dots).toHaveCSS('pointer-events', 'none');
});
