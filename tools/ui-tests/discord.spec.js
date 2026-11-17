const { test, expect } = require('@playwright/test');

test('Discord keeps its scroll position when opening a server, joining and refreshing', async ({ page }) => {
  await page.addInitScript(() => {
    const config = JSON.stringify({
      night: { enabled: false }, display: { idleReturnSec: 0 }, rules: [], shortcuts: [],
      pages: [{ id: 'discord-test', title: 'Discord', widgets: [
        { id: 'discord', type: 'discord', x: 0, y: 0, w: 12, h: 6, options: { control: true } },
      ] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  });
  await page.goto('/panel/');
  await page.evaluate(() => {
    const originalFetch = window.fetch;
    const snapshot = {
      connected: true, muted: false, deafened: false, channelId: null, channelName: null,
      members: [], notifications: [],
      channels: Array.from({ length: 24 }, (_, i) => ({
        id: `channel-${i}`, name: `Room ${i}`, guildId: `guild-${i}`, guild: `Server ${i}`,
      })),
    };
    window.discordTestSnapshot = snapshot;
    window.fetch = async (url, options) => {
      const path = new URL(String(url), location.href).pathname;
      if (path.endsWith('/api/discord')) return Response.json({ enabled: true, control: true, snapshot });
      if (path.endsWith('/api/discord/join')) {
        snapshot.channelId = new URL(String(url), location.href).searchParams.get('channel');
        snapshot.channelName = snapshot.channels.find(c => c.id === snapshot.channelId).name;
        return Response.json({ ok: true });
      }
      return originalFetch(url, options);
    };
  });
  const list = page.locator('.dc-servers');
  await expect(list.locator('.dc-server')).toHaveCount(24);
  const server = list.locator('.dc-server').filter({ hasText: 'Server 16' });
  await server.scrollIntoViewIfNeeded();
  const before = await list.evaluate(e => e.scrollTop);
  expect(before).toBeGreaterThan(200);
  await server.click();
  await expect.poll(() => list.evaluate(e => e.scrollTop)).toBeGreaterThan(200);
  const room = list.getByRole('button', { name: '🔊 Room 16', exact: true });
  await room.scrollIntoViewIfNeeded();
  await room.click();
  // Both the busy render and the completed request replace the list.
  expect(await list.evaluate(e => e.scrollTop)).toBeGreaterThan(200);
  await expect(page.locator('.dc-where')).toHaveText('Room 16');
  expect(await list.evaluate(e => e.scrollTop)).toBeGreaterThan(200);
  await page.evaluate(() => { window.discordTestSnapshot.muted = true; });
  await expect(page.locator('.dc-btn.mute')).toHaveClass(/\bon\b/);
  expect(await list.evaluate(e => e.scrollTop)).toBeGreaterThan(200);
  // The existing content-sized grid must also keep server cards from overlapping.
  expect(await list.locator('.dc-block:not(.open)').evaluateAll(blocks => blocks.every(block =>
    block.getBoundingClientRect().height >= block.firstElementChild.getBoundingClientRect().height
  ))).toBe(true);
});
