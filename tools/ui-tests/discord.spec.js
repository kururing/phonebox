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

// Discord widget: the demo answers /api/discord with you in a channel where Ada starts and stops speaking every 1.5 s.

const DISCORD = { id: 'd', type: 'discord', x: 0, y: 0, w: 6, h: 5, options: { control: true } };
const ONE_PAGE = [{ id: 'dc', title: 'Discord', enabled: true, widgets: [DISCORD] }];

async function show(page, { counting = false, pages = ONE_PAGE } = {}) {
  if (counting) {
    await page.addInitScript(() => {
      window.__dcRequests = 0;
      const wrap = () => {
        const f = window.fetch;
        if (!f || f.__dc) return;
        const g = (input, init) => {
          const url = typeof input === 'string' ? input : input.url;
          if (url.endsWith('/api/discord')) window.__dcRequests++;
          return f(input, init);
        };
        g.__dc = true;
        window.fetch = g;
      };
      setInterval(wrap, 10);
    });
  }
  await page.goto('/panel/');
  await page.evaluate((pages) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], pages }));
  }, pages);
  await page.reload();
}

test('a member who is speaking gets the ring, and loses it when they stop', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await show(page);
  const ada = page.locator('.dc-member', { hasText: 'Ada' });
  await expect(ada).toBeVisible();
  await expect(ada).toHaveClass(/speaking/, { timeout: 5000 });
  await expect(ada).not.toHaveClass(/speaking/, { timeout: 5000 });
  await expect(page.locator('.dc-member', { hasText: 'Linus' })).not.toHaveClass(/speaking/);
  expect(errors).toEqual([]);
});

test('the widget asks more often only while you are in a channel', async ({ page }) => {
  await show(page, { counting: true });
  await expect(page.locator('.dc-member').first()).toBeVisible();
  // in a channel: about every 500 ms (some 8 in 4 s; the bounds leave room for a busy test machine)
  let start = await page.evaluate(() => window.__dcRequests);
  await page.waitForTimeout(4000);
  expect(await page.evaluate(() => window.__dcRequests) - start).toBeGreaterThanOrEqual(5);
  // out of the channel: back to every 2 s
  await page.locator('.dc-btn.leave').click();
  await expect(page.locator('.dc-member')).toHaveCount(0);
  start = await page.evaluate(() => window.__dcRequests);
  await page.waitForTimeout(4000);
  expect(await page.evaluate(() => window.__dcRequests) - start).toBeLessThanOrEqual(3);
});

const requestsIn = async (page, ms) => {
  const start = await page.evaluate(() => window.__dcRequests);
  await page.waitForTimeout(ms);
  return await page.evaluate(() => window.__dcRequests) - start;
};

test('a widget on a neighbouring page does not ask until its page is shown', async ({ page }) => {
  // the panel keeps the pages next to the visible one up to date, so this one gets every update while off screen
  await show(page, { counting: true, pages: [
    { id: 'home', title: 'Home', enabled: true, widgets: [{ id: 'n', type: 'note', x: 0, y: 0, w: 6, h: 5, options: { text: 'Home' } }] },
    { id: 'dc', title: 'Discord', enabled: true, widgets: [DISCORD] },
  ] });
  await expect(page.locator('#dots i')).toHaveCount(2);
  expect(await requestsIn(page, 3000)).toBe(0);
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('#dots i').nth(1)).toHaveClass('on');
  await expect(page.locator('.dc-member', { hasText: 'Ada' })).toBeVisible();
  expect(await requestsIn(page, 3000)).toBeGreaterThanOrEqual(4);
});

test('a Discord card that is not showing in a Smart Stack does not ask', async ({ page }) => {
  await show(page, { counting: true, pages: [{ id: 'st', title: 'Stack', enabled: true, widgets: [{ id: 's', type: 'stack', x: 0, y: 0, w: 6, h: 5,
    options: { interval: 0, widgets: [{ id: 'n', type: 'note', options: { text: 'Front' } }, { id: 'd', type: 'discord', options: { control: true } }] } }] }] });
  await expect(page.locator('.stack-item.active .w-note')).toBeVisible();
  expect(await requestsIn(page, 3000)).toBe(0);
});

test('the input mode button switches between voice activity and push to talk', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await show(page);
  const mode = page.locator('.dc-btn.mode');
  await expect(mode).toBeVisible();
  await expect(mode).not.toHaveClass(/\bon\b/);
  await expect(mode).toHaveAttribute('aria-label', 'Switch to push to talk');
  await mode.click();
  await expect(mode).toHaveClass(/\bon\b/);
  await expect(mode).toHaveAttribute('aria-label', 'Switch to voice activity');
  await expect(page.locator('.dc-warn')).toHaveCount(0); // the demo has a push-to-talk key
  await mode.click();
  await expect(mode).not.toHaveClass(/\bon\b/);
  expect(errors).toEqual([]);
});

test('push to talk without a key in Discord shows a warning', async ({ page }) => {
  // the agent reports pttKey: false when Discord has no push-to-talk key; the demo always has one, so drop it here
  await page.addInitScript(() => {
    const wrap = () => {
      const f = window.fetch;
      if (!f || f.__nokey) return;
      const g = async (input, init) => {
        const r = await f(input, init);
        const url = typeof input === 'string' ? input : input.url;
        if (!url.endsWith('/api/discord')) return r;
        const j = await r.clone().json();
        if (j.snapshot) j.snapshot.pttKey = false;
        return new Response(JSON.stringify(j), { headers: { 'Content-Type': 'application/json' } });
      };
      g.__nokey = true;
      window.fetch = g;
    };
    setInterval(wrap, 10);
  });
  await show(page);
  await expect(page.locator('.dc-warn')).toHaveCount(0);
  await page.locator('.dc-btn.mode').click();
  await expect(page.locator('.dc-warn')).toHaveText(/no key is set in Discord/);
  await page.locator('.dc-btn.mode').click();
  await expect(page.locator('.dc-warn')).toHaveCount(0);
});

async function showWith(page, discord, widget) {
  await page.goto('/panel/');
  await page.evaluate(([discord, widget]) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], discord,
      pages: [{ id: 'dc', title: 'Discord', enabled: true, widgets: [widget] }] }));
  }, [discord, widget]);
  await page.reload();
}

test('without the widget option the buttons stay off, unless Allow Discord control is on', async ({ page }) => {
  const plain = { ...DISCORD, options: {} };
  await showWith(page, { allowControl: false }, plain);
  await expect(page.locator('.dc-btn.mute')).toBeDisabled();
  await showWith(page, { allowControl: true }, plain);
  await expect(page.locator('.dc-btn.mute')).toBeEnabled();
  await page.locator('.dc-btn.mute').click();
  await expect(page.locator('.dc-btn.mute')).toHaveClass(/\bon\b/);
});

test('a voice channel shows who is in it, and the server how many', async ({ page }) => {
  const withFavs = (options) => [{ ...ONE_PAGE[0], widgets: [{ ...DISCORD, options: { control: true, ...options } }] }];
  await show(page, { pages: withFavs({ favGuilds: ['g1'] }) });
  // the server you are in starts unfolded; the agent reports people for favourites only
  const lounge = page.locator('.dc-room', { hasText: 'Lounge' });
  await expect(lounge.locator('.dc-person', { hasText: 'Grace' })).toBeVisible();
  await expect(lounge.locator('.dc-person.muted', { hasText: 'Alan' })).toBeVisible();
  await expect(page.locator('.dc-server', { hasText: 'Friends' }).locator('.dc-invoice')).toHaveText(/2/);
  await show(page, { pages: withFavs({}) });
  await expect(page.locator('.dc-room', { hasText: 'Lounge' })).toBeVisible();
  await expect(page.locator('.dc-person')).toHaveCount(0);
});

// answers the favourite write with a status, after a delay, and counts how many were sent
async function favAnswers(page, status, delay = 0) {
  await page.addInitScript(([status, delay]) => {
    window.__favs = 0;
    const wrap = () => {
      const f = window.fetch;
      if (!f || f.__fav) return;
      const g = async (input, init) => {
        const url = typeof input === 'string' ? input : input.url;
        if (!url.includes('/discord-favorite')) return f(input, init);
        window.__favs++;
        await new Promise((done) => setTimeout(done, delay));
        return status === 200 ? f(input, init) : new Response(JSON.stringify({ error: 'changed' }), { status, headers: { 'Content-Type': 'application/json' } });
      };
      g.__fav = true;
      window.fetch = g;
    };
    setInterval(wrap, 10);
  }, [status, delay]);
}

test('a favourite saved over newer settings says so, and keeps the star as it was', async ({ page }) => {
  await favAnswers(page, 409);
  await show(page);
  await page.locator('.dc-favbtn').click();
  const star = page.locator('.dc-block', { hasText: 'Work' }).locator('.dc-headrow > .dc-star');
  await star.click();
  await expect(page.getByText('The settings were changed on the computer, try again')).toBeVisible();
  await expect(star).not.toHaveClass(/on/);
  await expect(star).toBeEnabled();
});

test('settings changed on the computer meanwhile are not written over', async ({ page }) => {
  await show(page);
  await page.locator('.dc-favbtn').click();
  const star = page.locator('.dc-block', { hasText: 'Work' }).locator('.dc-headrow > .dc-star');
  await expect(star).toBeVisible();
  // another save (the settings page): the panel has not read it yet when the star is tapped
  await page.evaluate(() => {
    localStorage.setItem('pb-demo-version', JSON.stringify(JSON.parse(localStorage.getItem('pb-demo-version') || '1') + 1));
    [...document.querySelectorAll('.dc-block')].find((b) => b.textContent.includes('Work')).querySelector('.dc-headrow > .dc-star').click();
  });
  await expect(page.getByText('The settings were changed on the computer, try again')).toBeVisible();
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].widgets[0].options.favGuilds)).toBeUndefined();
});

test('a favourite is saved after a settings read that overtook the save before it', async ({ page }) => {
  // the settings answer held back from before the save is applied while the save runs, so the panel holds the old version
  await page.addInitScript(() => {
    window.__statuses = [];
    const wrap = () => {
      const f = window.fetch;
      if (!f || f.__overtake) return;
      const g = async (input, init) => {
        const url = typeof input === 'string' ? input : input.url;
        if (!url.includes('/discord-favorite')) return f(input, init);
        if (!window.__overtaken) {
          window.__overtaken = true;
          await window.__loadConfig();
        }
        const r = await f(input, init);
        window.__statuses.push(r.status);
        return r;
      };
      g.__overtake = true;
      window.fetch = g;
    };
    setInterval(wrap, 10);
  });
  await show(page);
  await page.evaluate(async () => { const m = await import(document.querySelector('script[type=module]').src); window.__loadConfig = m.loadConfig; });
  await page.locator('.dc-favbtn').click();
  // tap the next star the moment the save lets go of it: the panel's regular refresh would bring the new version later
  await page.evaluate(async () => {
    const room = () => [...document.querySelectorAll('.dc-room')].find((r) => r.textContent.includes('Lounge'))?.querySelector('.dc-star');
    [...document.querySelectorAll('.dc-block')].find((b) => b.textContent.includes('Work')).querySelector('.dc-headrow > .dc-star').click();
    const until = Date.now() + 8000;
    while (Date.now() < until && (!window.__statuses.length || room()?.disabled)) await new Promise((r) => setTimeout(r, 5));
    if (!room()) document.querySelector('.dc-favbtn').click(); // the settings read again redrew the widget, out of its edit mode
    room().click();
  });
  await expect.poll(() => page.evaluate(() => window.__statuses)).toEqual([200, 200]);
});

test('a second tap while a favourite is being saved is ignored', async ({ page }) => {
  await favAnswers(page, 200, 1200);
  await show(page);
  await page.locator('.dc-favbtn').click();
  const star = page.locator('.dc-block', { hasText: 'Work' }).locator('.dc-headrow > .dc-star');
  await star.click();
  await expect(star).toBeDisabled();
  await star.evaluate((b) => b.click());
  await page.locator('.dc-room', { hasText: 'Lounge' }).locator('.dc-star').evaluate((b) => b.click());
  await expect(star).toHaveClass(/on/);
  await expect(star).toBeEnabled();
  expect(await page.evaluate(() => window.__favs)).toBe(1);
});

test('favourite servers and channels are picked on the phone and kept', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await show(page);
  await expect(page.locator('.dc-server', { hasText: 'Work' })).toBeVisible();
  await page.locator('.dc-favbtn').click();
  // star the Work server
  await page.locator('.dc-block', { hasText: 'Work' }).locator('.dc-headrow > .dc-star').click();
  await expect(page.locator('.dc-block', { hasText: 'Work' }).locator('.dc-headrow > .dc-star')).toHaveClass(/on/);
  // star one channel of the server you are in: that server then shows only it and the channel you are in
  await page.locator('.dc-room', { hasText: 'Lounge' }).locator('.dc-star').click();
  await page.locator('.dc-favbtn').click(); // Done
  await expect(page.locator('.dc-server', { hasText: 'Work' })).toBeVisible();
  await expect(page.locator('.dc-room')).toHaveCount(2); // Game night (you are in it) and Lounge
  // saved on the computer: still so after the panel reloads
  await page.reload();
  await expect(page.locator('.dc-room', { hasText: 'Lounge' })).toBeVisible();
  await expect(page.locator('.dc-room')).toHaveCount(2);
  expect(errors).toEqual([]);
});

test('favourites Discord no longer lists hide nothing, and can be removed', async ({ page }) => {
  await show(page, { pages: [{ ...ONE_PAGE[0], widgets: [{ ...DISCORD, options: { control: true, favGuilds: ['g9', 5, null, 'g9'] } }] }] });
  await expect(page.locator('.dc-server')).toHaveCount(2);
  await page.locator('.dc-favbtn').click();
  await expect(page.locator('.dc-goneid')).toHaveText(["g9No longer in Discord's list"]);
  await page.locator('.dc-gone .dc-star').click();
  await expect(page.locator('.dc-gone')).toHaveCount(0);
});

test('without favourites every server shows', async ({ page }) => {
  await show(page);
  await expect(page.locator('.dc-server')).toHaveCount(2);
  await expect(page.locator('.dc-room')).toHaveCount(2); // Friends is unfolded: Game night and Lounge
});

test('the ring moves without redrawing the widget, so avatars and server icons do not flash', async ({ page }) => {
  await show(page);
  const ada = page.locator('.dc-member', { hasText: 'Ada' });
  await expect(ada).toBeVisible();
  await page.locator('.w-discord .dc-ico').evaluateAll((icons) => icons.forEach((i) => { i.__kept = true; }));
  await expect(ada).toHaveClass(/speaking/, { timeout: 5000 });
  await expect(ada).not.toHaveClass(/speaking/, { timeout: 5000 });
  expect(await page.locator('.w-discord .dc-ico').evaluateAll((icons) => icons.every((i) => i.__kept === true))).toBe(true);
});
