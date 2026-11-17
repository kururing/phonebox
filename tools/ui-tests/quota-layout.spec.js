const { test, expect } = require('@playwright/test');

for (const viewport of [{ width: 780, height: 360 }, { width: 1280, height: 720 }, { width: 390, height: 740 }]) {
  test(`quota forecasts do not overlap session details at ${viewport.width}x${viewport.height}`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await page.addInitScript(() => {
      const config = JSON.stringify({ night: { enabled: false }, display: { idleReturnSec: 0 },
        pages: [{ id: 'quotas', title: 'Quotas', widgets: [
          { id: 'quota', type: 'quotas', x: 0, y: 0, w: 12, h: 6,
            options: { providers: ['codex', 'antigravity'], rotate: false, pageSize: 0 } },
        ] }],
      });
      localStorage.setItem('cfg', config);
      localStorage.setItem('pb-demo-config', config);
    });
    await page.goto('/panel/');
    const details = page.locator('.q-details').filter({ hasText: 'Latest local session' });
    await expect(details).toBeVisible();
    const bounds = await details.evaluate(e => {
      const card = e.closest('.q-card');
      const rings = card.querySelector('.q-rings');
      return { detailsTop: e.getBoundingClientRect().top,
        contentBottom: Math.max(...Array.from(rings.querySelectorAll('.q-ring')).map(r => r.getBoundingClientRect().bottom)),
        overflow: getComputedStyle(card).overflowY };
    });
    expect(bounds.detailsTop).toBeGreaterThanOrEqual(bounds.contentBottom - 1);
    expect(bounds.overflow).toBe('auto');
  });
}
