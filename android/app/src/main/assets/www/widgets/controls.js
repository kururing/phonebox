import { API, PANEL_HEADERS, api, applyState, bridge, buzz, curPage, D, evSeq, h, en, haptic, icon, nightOn, pages, sheet, toast, store, t } from '../main.js';
import { W } from '../registry.js';

let cached = null, at = 0, epoch = 0, pending = null;
export function invalidateDeckState() {
  epoch++;
  cached = null;
  at = 0;
  pending = null;
}
export async function deckState(force = false) {
  if (!force && cached && performance.now() - at < 650) return cached;
  if (!force && pending) return pending;
  const stamp = epoch;
  const task = api('/controls/state', 'GET', 15000).then(data => {
    if (stamp === epoch) { cached = data; at = performance.now(); }
    return data;
  }).finally(() => { if (pending === task) pending = null; });
  pending = task;
  return task;
}
export async function deckCommand(kind, values = {}) {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value != null) query.set(key, String(value));
  invalidateDeckState();
  const result = await api(`/controls/command/${encodeURIComponent(kind)}?${query}`, 'POST', 20000);
  if (!result.ok) throw new Error(result.error || 'Control is unavailable');
  invalidateDeckState();
  if (kind === 'action' || kind === 'sound-stop' || kind === 'media') {
    api('/state' + (evSeq != null ? '?ev=' + evSeq : '')).then(s => {
      if (s && (evSeq == null || s.evSeq == null || s.evSeq >= evSeq) && (!D.state?.ts || !s.ts || s.ts >= D.state.ts)) applyState(s);
    }).catch(() => {});
  }
  return result;
}
const fail = error => toast(error?.message || 'The computer did not answer', 'err');
const button = (label, click, name = label) => {
  const b = h('button', 'deck-button', label); b.type = 'button'; b.setAttribute('aria-label', name);
  b.onclick = async () => { haptic(); b.disabled = true; try { await click(); } catch (e) { fail(e); } finally { b.disabled = false; } };
  return b;
};
const selectedPlayer = (data, options) => {
  const chosen = options.player || store.get('control-player', '');
  return data.media?.find(p => p.id === chosen) || (!options.player ? data.media?.[0] : null);
};
function reconcile(root, nodes) {
  nodes.forEach((node, index) => { if (root.children[index] !== node) root.insertBefore(node, root.children[index] || null); });
  [...root.children].filter(node => !nodes.includes(node)).forEach(node => node.remove());
}
function empty(root, text) { root.replaceChildren(h('div', 'deck-empty', text)); }
function slider(label, send, max = 100) {
  const input = h('input', 'deck-slider'); input.type = 'range'; input.min = 0; input.max = max; input.step = 1;
  input.setAttribute('aria-label', label);
  const number = h('output', 'deck-value');
  let editing = false;
  // the track's filled part (style.css reads --p); min and max may be changed by the caller after creation
  const fill = () => { const lo = +input.min || 0, hi = +input.max || 100; input.style.setProperty('--p', (hi > lo ? Math.max(0, Math.min(100, (input.value - lo) / (hi - lo) * 100)) : 0) + '%'); };
  input.onpointerdown = () => { editing = true; };
  input.oninput = () => { number.textContent = input.value; fill(); };
  input.onchange = async () => { editing = false; try { await send(+input.value); } catch (e) { fail(e); } };
  // a touch that does not move the fader sends no "change": the fader must follow the computer again afterwards
  input.onpointerup = input.onpointercancel = input.onlostpointercapture = input.onblur = () => { editing = false; };
  return { input, number, set(value) { if (!editing) input.value = String(value); number.textContent = String(editing ? input.value : value); fill(); } };
}
function meter(value) { const e = h('div', 'deck-meter', h('i')); e.firstChild.style.setProperty('--peak', Math.max(0, Math.min(1, value || 0))); return e; }

function audioStateIcon(muted, microphone) {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  for (const [key, value] of Object.entries({ viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', 'stroke-width': '1.8', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true' })) svg.setAttribute(key, value);
  const shapes = microphone ? ['M9 5a3 3 0 0 1 6 0v7a3 3 0 0 1-6 0Z', 'M5 10v2a7 7 0 0 0 14 0v-2M12 19v3M8 22h8']
    : ['M11 4 6 8H3v8h3l5 4Z', ...(muted ? [] : ['M15 8a6 6 0 0 1 0 8M18 5a10 10 0 0 1 0 14'])];
  if (muted) shapes.push('M3 3 21 21');
  for (const d of shapes) { const path = document.createElementNS(ns, 'path'); path.setAttribute('d', d); svg.append(path); }
  return svg;
}

function audioView(box, options, mode) {
  const root = h('div', `deck-list deck-audio ${options.layout === 'vertical' ? 'deck-vertical' : ''}`);
  box.append(root);
  const rows = new Map();
  return data => {
    if (!data.audio?.available) { empty(root, data.audio?.error || 'Audio is unavailable on this computer'); return; }
    let items = mode === 'apps' || mode === 'app' ? data.audio.sessions || [] : data.audio.devices || [];
    if (mode === 'mic') items = items.filter(d => d.direction === 'input');
    if (mode === 'output') items = items.filter(d => d.direction === 'output');
    if (options.device) items = items.filter(d => d.id === options.device);
    if (options.application) { const want = options.application.toLowerCase(); items = items.filter(d => [d.name, d.processName].some(n => n?.toLowerCase().includes(want))); }
    if (mode === 'mic' && !options.device) items = items.filter(d => d.default);
    if (mode === 'balance') items = items.filter(d => d.direction === 'output' && d.channels >= 2 && (options.device || d.default));
    if (!items.length) { empty(root, mode === 'app' ? 'Start the selected application and play audio' : 'No matching audio devices'); return; }
    const nodes = items.map(item => {
      let row = rows.get(item.id);
      if (!row) {
        const el = h('div', 'deck-channel');
        const name = h('div', 'deck-channel-name', item.name);
        const signal = meter(item.peak);
        const kind = mode === 'app' || mode === 'apps' ? 'session' : 'device';
        const controls = slider(item.name + ' volume', value => deckCommand(kind, { id: item.id, value }));
        const mute = button('', () => deckCommand(kind, { id: item.id, muted: !row.item.muted }), 'Mute ' + item.name);

        mute.classList.add('deck-mute');
        const balance = [];
        el.append(name);
        if (mode === 'balance') {
          const left = slider('Left channel', value => deckCommand('device', { id: item.id, op: 'left', value }));
          const right = slider('Right channel', value => deckCommand('device', { id: item.id, op: 'right', value }));
          left.set(item.volume); right.set(item.volume);
          balance.push(left, right);
          el.append(h('label', '', 'L', left.input, left.number), h('label', '', 'R', right.input, right.number));
        } else if (mode !== 'meter') el.append(h('div', 'deck-volume-row', mute, h('div', 'deck-fader', signal, controls.input), controls.number));
        else el.append(signal);
        row = { el, name, signal, controls, mute, item, balance }; rows.set(item.id, row);
      }
      row.item = item; row.name.textContent = item.name;
      row.name.title = item.name;
      row.controls.set(item.volume);
      const muteAction = item.muted ? t('w.unmute') : t('w.mute');
      row.mute.replaceChildren(audioStateIcon(item.muted, mode === 'mic' || item.direction === 'input'));
      row.mute.title = muteAction;
      row.mute.setAttribute('aria-label', muteAction + ' ' + item.name);

      row.controls.input.disabled = item.canControl === false; row.mute.disabled = item.canControl === false;
      row.balance.forEach((slider, channel) => { const value = item.channelVolumes?.[channel]; slider.input.disabled = value == null; slider.set(value || 0); if (value == null) slider.number.textContent = 'n/a'; });
      row.mute.setAttribute('aria-pressed', String(item.muted));
      row.el.classList.toggle('deck-muted', item.muted); row.signal.firstChild.style.setProperty('--peak', Math.max(0, Math.min(1, item.peak || 0)));
      return row.el;
    });
    reconcile(root, nodes);
    for (const key of rows.keys()) if (!items.some(item => item.id === key)) rows.delete(key);
  };
}

function chooser(box, options, which) {
  const layout = ['horizontal', 'grid', 'vertical'].includes(options.layout) ? options.layout : 'vertical';
  const list = h('div', `deck-list deck-chooser deck-chooser-${layout}`); box.append(list);
  const labels = new Map();
  for (const line of String(options.labels || '').split(/\r?\n/)) {
    const spacedSeparator = line.lastIndexOf(' = ');
    const separator = spacedSeparator >= 0 ? spacedSeparator + 1 : line.indexOf('=');
    if (separator < 0) continue;
    const name = line.slice(0, separator).trim(), label = line.slice(separator + 1).trim();
    if (name && label) labels.set(name, label);
  }
  let signature = '';
  return data => {
    const items = which === 'media' ? data.media || [] : which === 'plans' ? data.powerPlans || [] : (data.audio?.devices || []).filter(d => d.direction === which);
    const role = options.role || 'all';
    const roleIndex = {console:0,media:1,communications:2}[role];
    const chosen = which === 'media' ? selectedPlayer(data, options)?.id : roleIndex != null && ['input','output'].includes(which)
      ? data.audio?.defaults?.[which+':'+roleIndex] : items.find(d => d.default || d.state === 'Active')?.id;
    const key = JSON.stringify([items.map(i => [i.id, i.name]), chosen, layout, [...labels]]); if (key === signature) return; signature = key;
    if (!items.length) { empty(list, which === 'media' ? 'Start a media player on the computer' : 'No compatible devices found'); return; }
    list.replaceChildren(...items.map(item => {
      const b = button(labels.get(item.name) || item.name, async () => {
        if (which === 'media') store.set('control-player', item.id);
        else await deckCommand(which === 'plans' ? 'power-plan' : 'default', { id: item.id, op:role });
        signature = '';
      }, item.name);
      b.title = item.name;
      b.classList.toggle('selected', item.id === chosen); b.setAttribute('aria-pressed', String(item.id === chosen)); return b;
    }));
  };
}

function hold(box, options, power = null) {
  const b = h('button', 'deck-hold', power ? `Hold: ${power}` : 'Hold to talk'); b.type = 'button';
  b.disabled = true;
  const status = h('div', 'deck-caption', power ? 'Hold for 2 seconds' : 'Release to restore the microphone'); box.append(b, status);
  let held = false, lease = null, timer = 0, renewal = 0, device = null;
  const end = async () => {
    held = false; clearTimeout(timer); clearInterval(renewal); b.classList.remove('held');
    const old = lease; lease = null;
    if (old && !power) try { await deckCommand('ptt', { op: 'end', lease: old }); } catch (_) { /* the server lease expires independently */ }
  };
  const begin = async event => {
    if (held || b.disabled) return;
    event.preventDefault(); held = true; b.classList.add('held');
    if (event.pointerId != null) try { b.setPointerCapture(event.pointerId); } catch (_) {}
    try {
      const result = await deckCommand(power ? 'power-arm' : 'ptt', { id: power || device, op: 'begin' });
      lease = result.lease;
      if (!held) { await end(); return; }
      if (power) timer = setTimeout(async () => {
        try { await deckCommand('power', { id: power, lease }); status.textContent = 'Power action scheduled'; } catch (e) { fail(e); }
        await end();
      }, 1700);
      else renewal = setInterval(() => deckCommand('ptt', { op: 'renew', lease }).catch(end), 1000);
    } catch (e) { fail(e); await end(); }
  };
  b.onpointerdown = begin; b.onpointerup = end; b.onpointercancel = end; b.onlostpointercapture = end;
  b.onkeydown = e => { if (!e.repeat && (e.key === ' ' || e.key === 'Enter')) begin(e); };
  b.onkeyup = end;
  const visibility = () => { if (document.hidden) end(); };
  const observer = typeof IntersectionObserver === 'function' ? new IntersectionObserver(entries => { if (entries.some(entry => !entry.isIntersecting)) end(); }) : null;
  observer?.observe(b);
  window.addEventListener('blur', end); document.addEventListener('visibilitychange', visibility);
  return {
    render(data) {
      device = options.device || data.audio?.devices?.find(d => d.direction === 'input' && d.default)?.id;
      b.disabled = power ? !data.allowPower : !device;
      if (b.disabled) status.textContent = power ? 'Enable power controls in desktop settings' : 'No microphone is available';
    },
    destroy() { end(); observer?.disconnect(); window.removeEventListener('blur', end); document.removeEventListener('visibilitychange', visibility); },
  };
}

function mediaView(box, options, mode) {
  const title = h('div', 'deck-track'), source = h('div', 'deck-caption'); box.append(title, source);
  let current = null;
  const call = (op, value) => current ? deckCommand('media', { id: current.id, op, value }) : Promise.reject(new Error('No media player selected'));
  const controls = h('div', 'deck-toolbar'); box.append(controls);
  let seek = null, shuffle = null, repeat = null;
  if (mode === 'seek') { seek = slider('Playback position', value => call('seek', value)); box.append(seek.input, seek.number); }
  else if (mode === 'modes') {
    shuffle = button('Shuffle', () => call('shuffle', current.shuffle ? 0 : 1));
    repeat = button('Repeat', () => call('repeat', current.repeat === 'None' ? 1 : current.repeat === 'Track' ? 2 : 0));
    controls.append(shuffle, repeat);
  } else controls.append(button(icon('skip-back'), () => call('prev'), 'Previous track'), button(icon('play'), () => call('playpause'), 'Play or pause'), button(icon('skip-forward'), () => call('next'), 'Next track'));
  return data => {
    current = selectedPlayer(data, options);
    title.textContent = current?.title || t('w.noMediaSelected'); source.textContent = current?.name || 'Choose a source in Media sources';
    controls.querySelectorAll('button').forEach(b => b.disabled = !current);
    if (seek) { seek.input.max = Math.max(1, current?.duration || 1); seek.set(current?.position || 0); seek.number.textContent = `${Math.floor((current?.position || 0) / 1000)} / ${Math.floor((current?.duration || 0) / 1000)} s`; seek.input.disabled = !current?.canSeek; }
    if (shuffle) { shuffle.disabled = !current?.canShuffle; shuffle.setAttribute('aria-pressed', String(!!current?.shuffle)); }
    if (repeat) { repeat.disabled = !current?.canRepeat; repeat.textContent = 'Repeat: ' + (current?.repeat || 'None'); }
  };
}

const groups = {
  'app-dock': ['app', 'shortcut'], hotkeys: ['keys'], 'macro-pad': ['macro'], 'window-layouts': ['window-layout'],
  'project-launcher': ['project', 'app'], snippets: ['snippet', 'text'], presentation: ['presentation'],
  'display-profiles': ['display-profile'], 'focus-profile': ['focus', 'macro'],
  'meeting-controls': ['meeting'], 'discord-controls': ['discord'], 'meeting-profile': ['meeting', 'macro'],
  'task-runner': ['task'], 'dev-servers': ['server'], services: ['service'], 'workflow-runner': ['workflow'], 'script-pad': ['task', 'shortcut'],
  soundboard: ['sound'], radio: ['radio'], playlists: ['playlist'], 'audio-routing': ['audio-route'],
  'external-mixer': ['mixer', 'mixer-mute'], 'midi-pad': ['midi'], 'osc-pad': ['osc'],
};
function actionsView(box, options, type) {
  const grid = h('div', 'deck-actions'); box.append(grid); let signature = '';
  return data => {
    const permitted = groups[type] || [];
    let items = [...(data.actions || []), ...(data.shortcuts || []).map(s => ({ ...s, id: 'shortcut:' + s.id }))]
      .filter(item => permitted.includes(item.kind));
    if(type === 'focus-profile' || type === 'meeting-profile') items.push(...(data.profiles || []));
    if (options.ids?.length) items = items.filter(item => options.ids.includes(item.id));
    const key = JSON.stringify(items); if (key === signature) return; signature = key;
    if (!items.length) { empty(grid, 'Assign controls in the computer’s Control Deck settings'); return; }
    grid.replaceChildren(...items.map(item => {
      const label = h('span', '', item.name), state = h('small', '', item.state || 'Ready');
      const b = button('', async () => { await deckCommand(item.kind === 'profile' ? 'profile' : 'action', { id: item.id, op: item.kind === 'radio' ? 'toggle' : (type === 'app-dock' ? 'focus' : '') }); signature = ''; });
      b.disabled = item.available === false;
      b.append(label, state);
      if (type === 'dev-servers') return h('div', 'deck-task', b, button('Stop', () => deckCommand('action', { id: item.id, op: 'stop' }), 'Stop ' + item.name));
      if (type === 'soundboard') return h('div', 'deck-task', b, button('Stop', () => deckCommand('sound-stop', { id: item.id }), 'Stop ' + item.name));
      return b;
    }));
  };
}

function radioInitials(label) {
  const parts = (label || 'FM').replace(/\([^)]*\)/g, '').trim().split(/[\s\-_.]+/).filter(Boolean);
  if (!parts.length) return 'FM';
  const chars = (s, n) => Array.from(s).slice(0, n).join('');
  return parts.length > 1 ? (chars(parts[0], 1) + chars(parts[1], 1)).toUpperCase() : chars(parts[0], 2).toUpperCase();
}

function radioView(box, options) {
  const isCarousel = options.view === 'carousel';
  box.classList.add('deck-radio-widget', isCarousel ? 'is-carousel' : 'is-grid');
  const wrap = h('div', isCarousel ? 'radio-carousel' : 'radio-grid-wrap');
  const track = h('div', isCarousel ? 'radio-carousel-track' : 'radio-grid');
  let prevBtn = null, nextBtn = null;

  const syncNav = () => {
    if (!isCarousel) return;
    const max = Math.max(0, track.scrollWidth - track.clientWidth);
    const canScroll = max > 6;
    wrap.classList.toggle('can-scroll', canScroll);
    if (prevBtn) prevBtn.disabled = !canScroll || track.scrollLeft <= 4;
    if (nextBtn) nextBtn.disabled = !canScroll || track.scrollLeft >= max - 4;
  };

  if (isCarousel) {
    const stepScroll = (dir) => {
      haptic();
      const step = Math.max(130, Math.round(track.clientWidth * 0.72));
      track.scrollBy({ left: dir * step, behavior: 'smooth' });
    };
    prevBtn = h('button', 'radio-nav radio-nav-prev', '‹');
    prevBtn.type = 'button';
    prevBtn.setAttribute('aria-label', 'Previous stations');
    prevBtn.onclick = (e) => { e.stopPropagation(); stepScroll(-1); };

    nextBtn = h('button', 'radio-nav radio-nav-next', '›');
    nextBtn.type = 'button';
    nextBtn.setAttribute('aria-label', 'Next stations');
    nextBtn.onclick = (e) => { e.stopPropagation(); stepScroll(1); };

    track.addEventListener('scroll', syncNav, { passive: true });

    let startX = 0, startY = 0;
    track.addEventListener('touchstart', (e) => {
      if (e.touches.length !== 1) return;
      startX = e.touches[0].clientX;
      startY = e.touches[0].clientY;
    }, { passive: true });
    track.addEventListener('touchmove', (e) => {
      if (e.touches.length !== 1) return;
      const dx = e.touches[0].clientX - startX;
      const dy = e.touches[0].clientY - startY;
      if (Math.abs(dx) <= Math.abs(dy)) return;
      const max = Math.max(0, track.scrollWidth - track.clientWidth);
      if (max <= 1 || (track.scrollLeft <= 0 && dx > 0) || (track.scrollLeft >= max - 1 && dx < 0)) {
        if (e.cancelable) e.preventDefault();
      }
    }, { passive: false });

    let dragDown = false, dragged = false, dragStartX = 0, dragScrollLeft = 0;
    track.addEventListener('pointerdown', (e) => {
      if (e.pointerType !== 'mouse' || e.button !== 0) return;
      dragDown = true; dragged = false;
      dragStartX = e.clientX; dragScrollLeft = track.scrollLeft;
    });
    track.addEventListener('pointermove', (e) => {
      if (!dragDown) return;
      const dx = e.clientX - dragStartX;
      if (Math.abs(dx) > 6) {
        dragged = true;
        track.classList.add('is-dragging');
      }
      if (dragged) track.scrollLeft = dragScrollLeft - dx;
    });
    const endDrag = () => {
      if (!dragDown) return;
      dragDown = false;
      track.classList.remove('is-dragging');
      if (dragged) setTimeout(() => { dragged = false; }, 0);
    };
    track.addEventListener('pointerup', endDrag);
    track.addEventListener('pointercancel', endDrag);
    track.addEventListener('pointerleave', endDrag);
    track.addEventListener('click', (e) => {
      if (dragged) { e.preventDefault(); e.stopPropagation(); dragged = false; }
    }, true);
    track.addEventListener('wheel', (e) => {
      if (Math.abs(e.deltaY) > Math.abs(e.deltaX) && track.scrollWidth > track.clientWidth) {
        const max = Math.max(0, track.scrollWidth - track.clientWidth);
        const canLeft = e.deltaY < 0 && track.scrollLeft > 0;
        const canRight = e.deltaY > 0 && track.scrollLeft < max;
        if (canLeft || canRight) {
          e.preventDefault();
          track.scrollLeft += e.deltaY;
        }
      }
    }, { passive: false });

    wrap.append(prevBtn, track, nextBtn);
  } else {
    wrap.append(track);
  }
  box.append(wrap);

  const cards = new Map();
  let latestItems = [];
  let optimisticId = null;
  let optimisticUntil = 0;
  let lastScrolledPlayingId = '';
  let cmdSeq = 0;
  let commandBusy = false;

  const effectivePlayingId = () => {
    if (optimisticId !== null && performance.now() < optimisticUntil) return optimisticId;
    optimisticId = null;
    return latestItems.find(i => i.state === 'Playing')?.id || '';
  };

  const syncCardsState = (allowAutoScroll) => {
    const activeId = effectivePlayingId();
    for (const [id, entry] of cards.entries()) {
      const playing = id === activeId;
      const unavail = entry.item.available === false;
      const subState = unavail && entry.item.state ? entry.item.state : '';
      entry.el.classList.toggle('is-playing', playing);
      entry.el.setAttribute('aria-pressed', String(playing));
      entry.el.disabled = unavail;
      entry.el.title = subState || entry.item.name;
      entry.name.textContent = subState ? `${entry.item.name} · ${subState}` : entry.item.name;
      entry.fallback.textContent = radioInitials(entry.item.name);
      entry.el.setAttribute('aria-label', entry.item.name + (playing ? ' (Playing)' : subState ? ` (${subState})` : ''));
    }
    if (isCarousel && allowAutoScroll && activeId && activeId !== lastScrolledPlayingId) {
      const activeEntry = cards.get(activeId);
      if (activeEntry) {
        const firstMount = !lastScrolledPlayingId;
        lastScrolledPlayingId = activeId;
        requestAnimationFrame(() => {
          const target = Math.max(0, activeEntry.el.offsetLeft - (track.clientWidth - activeEntry.el.offsetWidth) / 2);
          track.scrollTo({ left: target, behavior: firstMount ? 'auto' : 'smooth' });
          syncNav();
        });
      }
    } else if (!activeId) {
      lastScrolledPlayingId = '';
    }
  };

  const filterItems = (actions) => {
    const list = (actions || []).filter(item => item.kind === 'radio');
    if (!options.ids?.length) return list;
    const byId = new Map(list.map(item => [item.id, item]));
    return options.ids.map(id => byId.get(id)).filter(Boolean);
  };

  return (data) => {
    const items = filterItems(data.actions);
    latestItems = items;
    if (!items.length) {
      cards.clear();
      empty(track, 'Assign radio streams in the computer’s Control Deck settings');
      syncNav();
      return;
    }
    const emptyEl = track.querySelector('.deck-empty');
    if (emptyEl) emptyEl.remove();

    const nodes = items.map(item => {
      let entry = cards.get(item.id);
      if (!entry) {
        const fallback = h('span', 'radio-fallback', radioInitials(item.name));
        const img = h('img', 'radio-logo');
        img.alt = '';
        img.loading = 'lazy';
        img.decoding = 'async';
        img.draggable = false;
        const eq = h('span', 'radio-eq', h('i'), h('i'), h('i'));
        eq.setAttribute('aria-hidden', 'true');
        const logoBox = h('div', 'radio-logo-box', fallback, img, eq);
        img.onload = () => logoBox.classList.add('has-logo');
        img.onerror = () => logoBox.classList.remove('has-logo');

        const name = h('span', 'radio-name', item.name);
        const el = h('button', 'radio-card');
        el.type = 'button';
        el.append(logoBox, name);

        entry = { el, img, name, fallback, logoBox, item, busy: false, logoKey: '' };
        el.onclick = async () => {
          if (commandBusy || entry.busy) return;
          haptic();
          const seq = ++cmdSeq;
          const wasPlaying = effectivePlayingId() === entry.item.id;
          optimisticId = wasPlaying ? '' : entry.item.id;
          optimisticUntil = performance.now() + 3000;
          lastScrolledPlayingId = optimisticId;
          syncCardsState(false);
          if (isCarousel && optimisticId) {
            const target = Math.max(0, el.offsetLeft - (track.clientWidth - el.offsetWidth) / 2);
            track.scrollTo({ left: target, behavior: 'smooth' });
          }
          commandBusy = true;
          entry.busy = true;
          try {
            await deckCommand('action', { id: entry.item.id, op: 'toggle' });
            const fresh = await deckState(true);
            if (seq !== cmdSeq) return;
            latestItems = filterItems(fresh.actions);
            for (const next of latestItems) { const c = cards.get(next.id); if (c) c.item = next; }
            optimisticId = null; optimisticUntil = 0;
            syncCardsState(false);
          } catch (e) {
            if (seq === cmdSeq) {
              optimisticId = null; optimisticUntil = 0;
              syncCardsState(false);
            }
            fail(e);
          } finally {
            commandBusy = false;
            entry.busy = false;
          }
        };
        cards.set(item.id, entry);
      } else {
        entry.item = item;
      }
      return entry.el;
    });

    reconcile(track, nodes);
    for (const [id, entry] of cards.entries()) {
      const rev = entry.item.rev || entry.item.name || '';
      const nextKey = `${id}:${rev}`;
      if (entry.logoKey !== nextKey) {
        entry.logoKey = nextKey;
        entry.logoBox.classList.remove('has-logo');
        entry.img.src = `${API}/controls/radio-logo?id=${encodeURIComponent(id)}&v=${encodeURIComponent(rev)}`;
      }
    }
    for (const id of [...cards.keys()]) {
      if (!items.some(i => i.id === id)) cards.delete(id);
    }
    syncCardsState(true);
    requestAnimationFrame(syncNav);
  };
}

function mixerView(box,options){
  const root=h('div','deck-list');box.append(root);const rows=new Map();
  return data=>{
    const items=(data.actions || []).filter(a=>['mixer','mixer-mute'].includes(a.kind)&&(!options.ids?.length || options.ids.includes(a.id)));
    if(!items.length){empty(root,'Configure Voicemeeter bindings in desktop settings');return;}
    reconcile(root,items.map(item=>{
      let row=rows.get(item.id);
      if(!row){
        const el=h('div','deck-channel'),label=h('div','deck-channel-name',item.name),state=h('div','deck-caption');
        el.append(label,state);row={el,label,state,item};
        if(item.kind==='mixer'){
          row.slider=slider(item.name+' gain',value=>deckCommand('action',{id:item.id,value}),12);row.slider.input.min=-60;
          el.append(row.slider.input,row.slider.number);
        }else {row.toggle=button('Toggle',()=>deckCommand('action',{id:item.id,value:Number(row.item.state)>0?0:1}));el.append(row.toggle);}
        rows.set(item.id,row);
      }
      row.item=item;row.label.textContent=item.name;row.state.textContent=item.available===false?item.state || t('w.notConnected'):'';
      if(row.slider){row.slider.input.disabled=item.available===false;row.slider.set(Number(item.state)||0);row.slider.number.textContent=item.available===false?'n/a':item.state+' dB';}
      if(row.toggle){row.toggle.disabled=item.available===false;row.toggle.textContent=Number(item.state)>0?t('w.disable'):t('w.enable');row.toggle.setAttribute('aria-pressed',String(Number(item.state)>0));}
      return row.el;
    }));
    for(const id of rows.keys())if(!items.some(i=>i.id===id))rows.delete(id);
  };
}

function listening(box) {
  const state = h('div', 'deck-caption', t('w.listeningOff')); let ctl = null, context = null, next = 0, check = 0;
  const stop = () => { ctl?.abort(); ctl = null; context?.close(); context = null; clearInterval(check); toggle.textContent = t('w.listen'); state.textContent = t('w.listeningOff'); };
  const toggle = button(t('w.listen'), async () => {
    if (ctl) { stop(); return; }
    const Audio = window.AudioContext || window.webkitAudioContext;
    if (!Audio || !window.ReadableStream) throw new Error('This WebView does not support live audio');
    context = new Audio(); await context.resume(); ctl = new AbortController(); toggle.textContent = t('w.stopListening');
    state.textContent = t('w.listeningOn');
    const own = ctl;
    check = setInterval(() => { const r = box.getBoundingClientRect(); if (document.hidden || r.right <= 0 || r.left >= innerWidth) stop(); }, 250);
    (async () => {
      try {
        const response = await fetch(API + '/controls/listen', { headers: PANEL_HEADERS, signal: own.signal });
        if (!response.ok || !response.body) throw new Error('Listening is unavailable');
        const reader = response.body.getReader(), decoder = new TextDecoder(); let text = '';
        while (ctl === own) {
          const chunk = await reader.read(); if (chunk.done) break;
          text += decoder.decode(chunk.value, { stream: true });
          if (text.length > 1e6) throw new Error('Invalid audio stream');
          let newline;
          while ((newline = text.indexOf('\n')) >= 0) {
            const packet = JSON.parse(text.slice(0, newline)); text = text.slice(newline + 1);
            if (!packet.data || !context) continue;
            const raw = Uint8Array.from(atob(packet.data), c => c.charCodeAt(0)); const view = new DataView(raw.buffer);
            const bytes = packet.bits / 8, frames = raw.length / bytes / packet.channels;
            if (!Number.isInteger(frames) || ![16, 32].includes(packet.bits)) throw new Error('Unsupported audio format');
            const buffer = context.createBuffer(packet.channels, frames, packet.rate);
            for (let channel = 0; channel < packet.channels; channel++) {
              const samples = buffer.getChannelData(channel);
              for (let frame = 0; frame < frames; frame++) {
                const offset = (frame * packet.channels + channel) * bytes;
                samples[frame] = packet.encoding === 'IeeeFloat' ? view.getFloat32(offset, true)
                  : packet.bits === 16 ? view.getInt16(offset, true) / 32768 : view.getInt32(offset, true) / 2147483648;
              }
            }
            const source = context.createBufferSource(); source.buffer = buffer; source.connect(context.destination);
            if (next < context.currentTime || next > context.currentTime + 0.5) next = context.currentTime + 0.06;
            source.start(next); next += buffer.duration;
          }
        }
      } catch (e) { if (!own.signal.aborted) fail(e); }
      finally { if (ctl === own) stop(); }
    })();
  });
  toggle.disabled = true;
  box.append(toggle, state);
  return { render(data) { toggle.disabled = !data.allowListening; if (!data.allowListening) state.textContent = 'Enable listening in desktop settings'; }, destroy: stop };
}

export function registerDeckWidget(type, title, mode = type) {
  W[type] = {
    needs: ['tick'],
    create(box, { o }) {
      box.classList.add('control-deck'); box.append(h('div', 'w-title', o.title || title));
      let render = null, component = null, alive = true, loading = false;
      if (['apps', 'app', 'mic', 'output', 'balance', 'meter'].includes(mode)) render = audioView(box, o, mode);
      else if(mode === 'external-mixer')render=mixerView(box,o);
      else if (mode === 'inputs' || mode === 'outputs') render = chooser(box, o, mode === 'inputs' ? 'input' : 'output');
      else if (mode === 'sources' || mode === 'plans') render = chooser(box, o, mode === 'sources' ? 'media' : 'plans');
      else if (mode === 'ptt') component = hold(box, o);
      else if (mode === 'listen') component = listening(box);
      else if (['remote', 'seek', 'modes'].includes(mode)) render = mediaView(box, o, mode);
      else if (mode === 'panic') box.append(button('Mute audio + microphones', () => deckCommand('panic')));
      else if (mode === 'desktops') box.append(button('← Desktop', () => deckCommand('desktop', { value: -1 })), button('Desktop →', () => deckCommand('desktop', { value: 1 })));
      else if (mode === 'system') box.append(button('Lock', () => deckCommand('lock')), button('Display off', () => deckCommand('monitor-off')));
      else if (mode === 'power') {
        const group = h('div', 'deck-power-grid'); box.append(group);
        const components = ['sleep', 'restart', 'shutdown'].map(action => { const item = h('div', 'deck-power-item'); group.append(item); return hold(item, o, action); });
        component = { render(data) { components.forEach(c => c.render(data)); }, destroy() { components.forEach(c => c.destroy()); } };
      } else if (mode === 'duck') {
        let enabled = false; const b = button('Enable ducking', () => deckCommand('duck', { op: enabled ? 'off' : 'on', value: o.level ?? 25 })); box.append(b);
        render = data => { enabled = data.ducking; b.textContent = enabled ? t('w.disableDucking') : t('w.enableDucking'); b.setAttribute('aria-pressed', String(enabled)); };
      } else if (mode === 'profiles') {
        const list = h('div', 'deck-actions'); box.append(list); let key = '';
        render = data => { const next = JSON.stringify(data.profiles); if (next === key) return; key = next;
          if (!data.profiles?.length) empty(list, 'Save an audio profile in desktop settings');
          else list.replaceChildren(...data.profiles.map(p => button(p.name, () => deckCommand('profile', { id: p.id })))); };
      } else if (mode === 'sleep') {
        let player = null; const caption = h('div', 'deck-caption');
        box.append(button(`Sleep in ${o.minutes || 30} min`, () => deckCommand('sleep', { id: player?.id || '', value: o.minutes || 30 })),
          button('Cancel timer', () => deckCommand('sleep', { id: player?.id || '', value: 0 })), caption);
        render = data => { player = selectedPlayer(data, o); const timer = data.sleep?.find(t => t.id === player?.id);
          caption.textContent = timer ? `Pausing ${player?.name || 'player'} in ${Math.max(0, Math.ceil((timer.at - Date.now()) / 60000))} min` : t('w.noSleepTimer'); };
      } else if (mode === 'brightness') {
        const list = h('div', 'deck-list'); box.append(list); const rows = new Map();
        render = data => {
          const items = (data.monitors || []).filter(m => !o.device || m.id === o.device);
          if (!items.length) { empty(list, t('w.noMonitors')); return; }
          reconcile(list, items.map(m => { if (!rows.has(m.id)) { const s = slider(m.name + ' brightness', value => deckCommand('brightness', { id: m.id, value }));
            rows.set(m.id, { ...s, el: h('div', 'deck-channel', h('div', 'deck-channel-name', m.name), s.input, s.number) }); }
            const row = rows.get(m.id); row.input.disabled = m.brightness == null; row.set(m.brightness || 0); if (m.brightness == null) row.number.textContent = 'DDC/CI unavailable'; return row.el; }));
        };
      } else if (mode === 'radio') render = radioView(box, o);
      else render = actionsView(box, o, type);
      return {
        async update() {
          if (loading || !alive || (!render && !component)) return;
          loading = true;
          try { const data = await deckState(); if (alive) { render?.(data); component?.render(data); box.classList.remove('deck-offline'); } }
          catch (_) { if (alive) box.classList.add('deck-offline'); }
          finally { loading = false; }
        },
        destroy() { alive = false; component?.destroy(); },
      };
    },
  };
}

for (const [type, title, mode] of [
  ['audio-mixer', 'Application mixer', 'apps'], ['app-volume', 'Application volume', 'app'],
  ['device-volume', 'Device volume', 'output'], ['audio-balance', 'Channel balance', 'balance'], ['audio-meter', 'Audio levels', 'meter'],
  ['panic-mute', 'Emergency mute', 'panic'], ['microphone', 'Microphone', 'mic'], ['audio-inputs', 'Microphone selection', 'inputs'],
  ['audio-outputs', 'Output selection', 'outputs'], ['push-to-talk', 'Push to talk', 'ptt'], ['audio-profiles', 'Audio profiles', 'profiles'],
  ['audio-ducking', 'Audio ducking', 'duck'], ['media-sources', 'Media sources', 'sources'], ['media-remote', 'Media remote', 'remote'],
  ['media-seek', 'Playback position', 'seek'], ['media-modes', 'Shuffle and repeat', 'modes'], ['media-sleep', 'Music sleep timer', 'sleep'],
  ['brightness', 'Display brightness', 'brightness'], ['power-plans', 'Power plans', 'plans'], ['desktops', 'Virtual desktops', 'desktops'],
  ['system-keys', 'Lock and displays', 'system'], ['power-controls', 'Power controls', 'power'], ['pc-listen', 'Listen to PC audio', 'listen'],
]) registerDeckWidget(type, title, mode);
for (const type of Object.keys(groups)) registerDeckWidget(type, type.split('-').map(s => s[0].toUpperCase() + s.slice(1)).join(' '));

W['meeting-status'] = { needs: ['state', 'push'], create: (box, ctx) => W.onair.create(box, ctx) };

async function integrationCommand(path, values = {}) {
  const result = await api('/controls/' + path + '?' + new URLSearchParams(values), 'POST', 20000);
  if (!result.ok) throw new Error(result.error || 'Integration refused this control');
  return result;
}
function integrationWidget(type, title, endpoint, create) {
  W[type] = { needs: ['tick'], create(box, {o}) {
    box.classList.add('control-deck'); box.append(h('div', 'w-title', o.title || title));
    const root = h('div', 'deck-list'); box.append(root);
    let alive = true, busy = false, signature = '', data = null;
    return { async update() {
      if (!alive || busy) return; busy = true;
      try {
        data = await api('/controls/' + endpoint, 'GET', 20000); if (!alive) return;
        if (root.contains(document.activeElement) && document.activeElement.matches('input,textarea,select') || root.matches(':active') || root.querySelector(':active')) return;
        const key = JSON.stringify(data); if (key === signature) return; signature = key;
        if (!data.connected) { empty(root, data.error || 'Connect this integration in desktop settings'); return; }
        root.replaceChildren();
        create(root, data, (action, args) => integrationCommand(endpoint === 'obs' ? 'obs/' + action : 'spotify/action/' + action, args).then(()=>{signature='';}), o);
      } catch (error) { if (alive) empty(root, error.message || 'Integration is unavailable'); }
      finally { busy = false; }
    }, destroy() { alive = false; } };
  } };
}
const timeText = ms => { const s = Math.floor((ms || 0) / 1000); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); };
integrationWidget('obs-mixer', 'OBS mixer', 'obs', (root, data, send) => {
  if (!data.inputs?.length) { empty(root, 'No OBS inputs found'); return; }
  for (const input of data.inputs) {
    const s = slider(input.name + ' volume', value => send('volume', {id:input.id,value}));
    s.set(input.volume || 0); s.input.disabled = input.volume == null;
    const mute = button(input.muted ? 'Unmute' : 'Mute', ()=>send('mute',{id:input.id,enabled:!input.muted}), 'Mute ' + input.name);
    mute.disabled = input.muted == null;
    root.append(h('div','deck-channel',h('div','deck-channel-name',input.name),s.input,h('div','deck-channel-foot',s.number,mute)));
  }
});
integrationWidget('obs-scenes', 'OBS scenes', 'obs', (root, data, send) => {
  for (const name of data.scenes || []) { const b=button(name,()=>send('scene',{id:name}));b.classList.toggle('selected',name===data.scene);root.append(b); }
});
integrationWidget('obs-sources', 'OBS sources', 'obs', (root, data, send) => {
  if (!data.sources?.length) empty(root,'No sources in the current scene');
  for(const source of data.sources || []) { const b=button((source.enabled?'● ':'○ ')+source.name,()=>send('source',{id:source.id,enabled:!source.enabled}));b.setAttribute('aria-pressed',String(source.enabled));root.append(b); }
});
integrationWidget('obs-recording', 'OBS recording and stream', 'obs', (root,data,send)=>{
  root.append(h('div','deck-track','Recording '+timeText(data.recordMs)),h('div','deck-caption','Stream '+timeText(data.streamMs)),
    button(data.recording?'Stop recording':'Start recording',()=>send('record')),
    button(data.streaming?'Stop stream':'Start stream',()=>send('stream')));
  const pause=button(data.recordPaused?'Resume recording':'Pause recording',()=>send('pause'));pause.disabled=!data.recording;root.append(pause);
});
integrationWidget('obs-replay', 'OBS replay buffer', 'obs', (root,data,send)=>{
  const toggle=button(data.replayActive?'Stop replay buffer':'Start replay buffer',()=>send('replay'));toggle.disabled=!data.replayAvailable;
  const save=button('Save replay',()=>send('save-replay'));save.disabled=!data.replayActive;root.append(toggle,save);
});
integrationWidget('obs-transition', 'OBS transition', 'obs', (root,data,send)=>{
  for(const t of data.transitions || []) {const b=button(t.transitionName,()=>send('transition',{id:t.transitionName}));b.classList.toggle('selected',data.transition===t.transitionName);root.append(b);}
  const s=slider('Transition duration',value=>send('duration',{value}),10000);s.input.min=50;s.input.step=50;s.set(data.duration || 300);s.input.disabled=data.duration==null;root.append(s.input,s.number);
});
integrationWidget('obs-camera', 'OBS virtual camera', 'obs', (root,data,send)=>{
  const b=button(data.cameraActive?'Stop virtual camera':'Start virtual camera',()=>send('camera'));b.disabled=!data.cameraAvailable;root.append(b);
});
// Spotify's image server only (the agent passes nothing else); the phone loads covers itself while the option is on.
const SPOTIFY_COVER = /^https:\/\/i\.scdn\.co\/image\/[A-Za-z0-9]+$/;
integrationWidget('media-queue', 'Spotify queue', 'spotify/state', (root,data,send,o)=>{
  const cover = (url) => {
    if (o.covers === false) return null; // no cover column at all: more room for the titles
    const c = h('span','sq-cover');
    if (SPOTIFY_COVER.test(url || '')) {
      const img = h('img'); img.alt = ''; img.decoding = 'async'; img.referrerPolicy = 'no-referrer';
      img.onerror = () => img.remove(); img.src = url; c.append(img);
    }
    return c;
  };
  const text = (name, artist) => h('div','sq-txt', h('div','sq-name',name || ''), artist ? h('div','sq-artist',artist) : null);
  const cur = data.current;
  root.classList.add('sq');
  root.append(h('div','sq-now' + (data.playing ? ' playing' : ''), cover(cur?.image),
    h('div','sq-txt', h('div','sq-label', t(cur ? 'w.nowPlaying' : 'w.nothingPlaying')),
      cur ? h('div','sq-name',cur.name || '') : null, cur?.artist ? h('div','sq-artist',cur.artist) : null)));
  if (!data.queue?.length) { root.append(h('div','deck-empty',t('w.queueEmpty'))); return; }
  root.append(h('div','sq-head',t('w.upNext')));
  for (const item of data.queue) {
    const b = h('button','sq-item', cover(item.image), text(item.name, item.artist));
    b.type = 'button';
    b.setAttribute('aria-label', item.name + (item.artist ? ' · ' + item.artist : ''));
    b.onclick = () => { haptic(); send('play-queued',{id:item.id}).catch(fail); };
    root.append(b);
  }
});
integrationWidget('media-favorites', 'Spotify favorites', 'spotify/state', (root,data,send)=>{
  root.append(h('div','deck-track',data.current?.name || t('w.nothingPlaying')),h('div','deck-caption',data.current?.artist || ''));
  const b=button('Save to library',()=>send('favorite'));b.disabled=!data.current?.id;root.append(b);
});
W['meeting-timer'] = {needs:['tick'],create(box,{o}){
  const key='meeting-timer:'+ (o.timerId || 'default');
  const clock=h('div','deck-track');box.classList.add('control-deck');box.append(h('div','w-title',o.title || en('Meeting timer')),clock);
  let state=store.get(key,{elapsed:0,started:0});
  const persist=()=>store.set(key,state);
  const start=button(state.started?'Pause':'Start',()=>{state=store.get(key,state);if(state.started){state.elapsed+=Date.now()-state.started;state.started=0;}else state.started=Date.now();persist();render();});
  box.append(start,button('Reset',()=>{state={elapsed:0,started:0};persist();render();}));
  function render(){state=store.get(key,state);const elapsed=state.elapsed+(state.started?Date.now()-state.started:0);clock.textContent=timeText(elapsed);start.textContent=state.started?t('w.pause'):t('w.start');start.setAttribute('aria-label',start.textContent);clock.classList.toggle('deck-overdue',elapsed>=(o.minutes || 30)*60000);}
  render();return {update:render};
}};

W.trackpad = {
  needs: [],
  create(box, { o }) {
    box.classList.add('control-deck');
    const controlEnabled = Boolean(o.control);
    const sensitivity = Number(o.sensitivity) || 1.0;
    const acceleration = o.acceleration !== false;
    const scrollSpeed = Number(o.scrollSpeed) || 1.0;
    const natural = Boolean(o.naturalScroll);
    const holdDuration = Math.max(100, Number(o.holdDuration) || 350);
    const scrollBar = o.scrollBar || 'right';
    const scrollBarWidth = Math.max(16, Number(o.scrollBarWidth) || 36);
    // A different feel for each thing the pad does: a tick for a click, two for a right click, a firmer press when a
    // drag starts, a longer one when a gesture fires, and a light tick for every notch of a scroll. "Haptic feedback"
    // sets how strong they are (or turns them off) and "Tick while scrolling" turns the scroll ticks on (off by default).
    // They go through the panel's buzz(), so night mode and the display's vibration switch silence them too. The gestures
    // vibrate only for a Mac: the agent ignores them on any other computer, where a vibration would suggest something happened.
    const strength = o.haptics === 'off' ? 0 : ({ light: 0.6, strong: 1.8 }[o.haptics] ?? 1);
    const scrollTicks = o.scrollHaptics === true;
    const patterns = { click: '0,14', rightClick: '0,14,70,14', drag: '0,30', swipe: '0,20,45,20', spread: '0,45', pinch: '0,45', scroll: '0,8' };
    const feel = (kind) => {
      if (!strength || (kind === 'scroll' && !scrollTicks) || !patterns[kind]) return;
      if ((kind === 'swipe' || kind === 'spread' || kind === 'pinch') && D.state?.os !== 'macos') return;
      // only the vibrations get weaker or stronger, not the pauses between them
      const pattern = patterns[kind].split(',').map((ms, i) => (i % 2 ? Math.max(4, Math.round(Number(ms) * strength)) : Number(ms))).join(',');
      buzz(pattern);
    };

    const surface = h('div', 'deck-trackpad-surface',
      h('div', 'deck-trackpad-icon', icon(controlEnabled ? 'mouse' : 'lock')),
      h('div', 'deck-trackpad-hint', !controlEnabled
        ? 'Enable mouse control in widget settings'
        : (scrollBar !== 'none'
          ? 'Drag to move · Tap to click · Hold to drag · Bar or 2 fingers to scroll'
          : 'Drag to move · Tap to click · Hold to drag · Two fingers to scroll'))
    );

    let scrollBarEl = null;
    if (controlEnabled && scrollBar !== 'none') {
      scrollBarEl = h('div', 'deck-trackpad-bar',
        h('div', 'deck-trackpad-bar-icon',
          h('span', null, '▲'),
          h('span', null, '▼')
        )
      );
      scrollBarEl.style.width = scrollBarWidth + 'px';
    }

    const pad = h('div', 'deck-trackpad' + (scrollBar === 'left' ? ' has-scroll-left' : ''), surface);
    pad.setAttribute('tabindex', '0');
    pad.setAttribute('role', 'region');
    pad.setAttribute('aria-label', o.title || 'Trackpad');
    if (scrollBarEl) pad.append(scrollBarEl);
    box.append(h('div', 'w-title', o.title || 'Trackpad'), pad);

    if (!controlEnabled) {
      pad.classList.add('is-disabled');
      return { update() {}, destroy() {} };
    }

    let ws = null, destroyed = false, reconnectTimer = null;
    function getWsUrl() {
      const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
      if (location.host && !location.host.includes('androidplatform.net')) return `${proto}//${location.host}/api/trackpad`;
      return 'ws://127.0.0.1:8765/api/trackpad';
    }
    function connectWs() {
      if (destroyed || (bridge && typeof bridge.trackpad === 'function')) return;
      try {
        ws = new WebSocket(getWsUrl());
        ws.onclose = () => { if (!destroyed) reconnectTimer = setTimeout(connectWs, 2000); };
        ws.onerror = () => { try { ws.close(); } catch (_) {} };
      } catch (_) {
        if (!destroyed) reconnectTimer = setTimeout(connectWs, 2000);
      }
    }
    connectWs();

    function send(msg) {
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify(msg));
      } else if (bridge && typeof bridge.trackpad === 'function') {
        bridge.trackpad(msg.type || '', msg.dx || 0, msg.dy || 0, msg.delta ?? msg.dy ?? 0);
      } else {
        fetch('/api/trackpad', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(msg),
          cache: 'no-store'
        }).catch(() => {});
      }
    }

    // Windows Precision Touchpad ballistics curve: 3.5x base gain with controlled +1.6x dynamic flick boost
    function getGain(v) {
      const vp = v * v;
      return 3.5 + (1.6 * vp) / (2.0 + vp);
    }

    let accDx = 0, accDy = 0, moveScheduled = false;
    function queueMove(dx, dy) {
      accDx += dx; accDy += dy;
      if (!moveScheduled) {
        moveScheduled = true;
        requestAnimationFrame(() => {
          moveScheduled = false;
          flushMove();
        });
      }
    }
    function flushMove() {
      if (destroyed) return;
      moveScheduled = false;
      const sendDx = Math.round(accDx), sendDy = Math.round(accDy);
      accDx -= sendDx; accDy -= sendDy;
      if (sendDx !== 0 || sendDy !== 0) send({ type: 'move', dx: sendDx, dy: sendDy });
    }

    let accScrollX = 0, accScrollY = 0, scrollScheduled = false;
    function queueScroll(dx, dy) {
      accScrollX += dx;
      accScrollY += dy;
      if (!scrollScheduled) {
        scrollScheduled = true;
        requestAnimationFrame(() => {
          scrollScheduled = false;
          flushScroll();
        });
      }
    }
    function flushScroll() {
      if (destroyed) return;
      scrollScheduled = false;
      const sx = Math.round(accScrollX);
      const sy = Math.round(accScrollY);
      accScrollX -= sx;
      accScrollY -= sy;
      if (sx !== 0 || sy !== 0) { send({ type: 'scroll', dx: sx, dy: sy, delta: sy }); scrollFeel(sx, sy); }
    }
    // a light tick for each notch of a wheel (120), at most one every 45 ms so a fast scroll hums instead of buzzing
    let scrollSince = 0, lastScrollTick = 0;
    function scrollFeel(dx, dy) {
      scrollSince += Math.abs(dx) + Math.abs(dy);
      const now = performance.now();
      if (scrollSince < 120 || now - lastScrollTick < 45) return;
      scrollSince %= 120;
      lastScrollTick = now;
      feel('scroll');
    }

    const activePointers = new Map();
    let maxActivePointers = 0, moved = false, tapFired = false;
    let isDragging = false, holdTimer = null;
    let scrollAxis = null, cumScrollX = 0, cumScrollY = 0;

    // Three or more fingers make a gesture, as on a Mac's trackpad: a swipe (3 or 4 fingers) or, with 4 or more, a spread
    // or pinch. The agent decides what it does from the Mac's own Trackpad settings and ignores it on other computers.
    let gestureBase = null, gestureFired = false, gestureCheckScheduled = false, gestureQuietTimer = null;
    const touches = () => [...activePointers.values()].filter((p) => !p.isBar);
    function gestureShape() {
      const ps = touches(), n = ps.length;
      let cx = 0, cy = 0, r = 0;
      for (const p of ps) { cx += p.lastX; cy += p.lastY; }
      cx /= n; cy /= n;
      for (const p of ps) r += Math.hypot(p.lastX - cx, p.lastY - cy);
      return { n, cx, cy, r: r / n };
    }
    // measure from here: with three or more fingers down, or no gesture to judge
    function rebase() {
      clearTimeout(gestureQuietTimer);
      const ps = touches();
      gestureBase = !gestureFired && ps.length >= 3 ? gestureShape() : null;
      for (const p of ps) { p.baseX = p.lastX; p.baseY = p.lastY; }
    }
    // 'out' when every finger that has moved went mostly away from the middle it started around, 'in' when mostly towards it
    function radialMotion(b) {
      let out = 0, inward = 0, other = 0;
      for (const p of touches()) {
        const dx = p.lastX - p.baseX, dy = p.lastY - p.baseY, d = Math.hypot(dx, dy);
        if (d <= 12) continue; // it rests
        const rx = p.baseX - b.cx, ry = p.baseY - b.cy, r = Math.hypot(rx, ry);
        if (r < 8) continue; // it started in the middle: there is no away or towards, so it neither helps nor hinders
        const along = (dx * rx + dy * ry) / (d * r);
        if (along >= 0.6) out++; else if (along <= -0.6) inward++; else other++;
      }
      return other === 0 && out > 0 && inward === 0 ? 'out' : other === 0 && inward > 0 && out === 0 ? 'in' : null;
    }
    // A swipe is judged as it goes, from how far the fingers' middle has moved. A spread or pinch is judged only when the fingers
    // have finished (one lifts, or they have been still for a moment): while they are moving, a frame can show some of them
    // moved and the others not yet, which is a partial sample that looks like one.
    function checkGesture(final = false) {
      const g = gestureShape(), b = gestureBase;
      if (g.n !== b.n) { rebase(); return; } // a finger landed or lifted: measure again from here
      const dx = g.cx - b.cx, dy = g.cy - b.cy, dist = Math.hypot(dx, dy);
      const ratio = b.r > 8 ? g.r / b.r : 1;
      // A spread or pinch is the fingers moving apart or together, so at least half of them must have moved: one finger that
      // has moved before the others (a frame, a pause, a finger lifting) is not one, and fingers that rest do not stop it.
      const together = touches().filter((p) => Math.hypot(p.lastX - p.baseX, p.lastY - p.baseY) > 12).length >= Math.ceil(g.n / 2);
      // and the way they moved: away from the middle for a spread, towards it for a pinch. One finger that moved along
      // (up, say, while the others have yet to follow) changed the spread of the fingers but did not open them.
      const way = radialMotion(b);
      let type = null;
      if (final && g.n >= 4 && together && way === 'out' && dist < 45 && ratio > 1.35) type = 'spread';
      else if (final && g.n >= 4 && together && way === 'in' && dist < 45 && ratio < 0.72) type = 'pinch';
      else if (dist > 60) type = 'swipe' + Math.min(g.n, 4) + (Math.abs(dx) > Math.abs(dy) ? (dx < 0 ? 'left' : 'right') : (dy < 0 ? 'up' : 'down'));
      if (!type) return;
      gestureFired = true;
      clearTimeout(gestureQuietTimer);
      moved = true;
      send({ type });
      feel(type.startsWith('swipe') ? 'swipe' : type);
    }

    pad.addEventListener('pointerdown', (e) => {
      e.stopPropagation(); e.preventDefault();
      try { pad.setPointerCapture(e.pointerId); } catch (_) {}
      const isBar = scrollBarEl && (e.target === scrollBarEl || scrollBarEl.contains(e.target));
      if (isBar) scrollBarEl.classList.add('is-scrolling');
      const now = performance.now();
      activePointers.set(e.pointerId, {
        startX: e.clientX, startY: e.clientY,
        lastX: e.clientX, lastY: e.clientY,
        downTime: now,
        lastMoveTime: now,
        history: [{ t: now, x: e.clientX, y: e.clientY }],
        isBar,
      });
      maxActivePointers = Math.max(maxActivePointers, activePointers.size);
      if (activePointers.size > 1) {
        if (holdTimer) { clearTimeout(holdTimer); holdTimer = null; }
        if (isDragging) {
          isDragging = false;
          send({ type: 'mouseup' });
        }
        scrollAxis = null;
        cumScrollX = 0;
        cumScrollY = 0;
      }
      if (!isBar) rebase();
      if (!isBar && activePointers.size === 1) {
        moved = false;
        tapFired = false;
        holdTimer = setTimeout(() => {
          if (!moved && !tapFired && maxActivePointers === 1 && activePointers.size === 1 && !isDragging) {
            isDragging = true;
            flushMove();
            send({ type: 'mousedown' });
            feel('drag');
          }
        }, holdDuration);
      }
    });

    pad.addEventListener('pointermove', (e) => {
      e.stopPropagation(); e.preventDefault();
      const pt = activePointers.get(e.pointerId);
      if (!pt) return;
      const rawDx = e.clientX - pt.lastX, rawDy = e.clientY - pt.lastY;
      pt.lastX = e.clientX; pt.lastY = e.clientY;
      // after a gesture fires, the fingers still down do nothing until all of them lift (no scroll, no pointer)
      if (gestureFired) return;

      if (pt.isBar) {
        const factor = natural ? 1 : -1;
        queueScroll(0, rawDy * scrollSpeed * 10 * factor);
        return;
      }

      if (Math.hypot(e.clientX - pt.startX, e.clientY - pt.startY) > 6) {
        moved = true;
        if (!isDragging && holdTimer) { clearTimeout(holdTimer); holdTimer = null; }
      }

      // a finger's pointermove comes one at a time, so look at the fingers together once per frame, not between two of them
      if (gestureBase && !gestureFired && !gestureCheckScheduled) {
        gestureCheckScheduled = true;
        requestAnimationFrame(() => {
          gestureCheckScheduled = false;
          if (!destroyed && gestureBase && !gestureFired) checkGesture();
        });
      }
      clearTimeout(gestureQuietTimer);
      if (gestureBase && !gestureFired) {
        gestureQuietTimer = setTimeout(() => { if (!destroyed && gestureBase && !gestureFired) checkGesture(true); }, 90);
      }

      if (activePointers.size === 1) {
        let gain = 1.0;
        if (acceleration) {
          const now = performance.now();
          const elapsed = now - pt.lastMoveTime;
          if (elapsed > 60) {
            const dt = Math.max(12, Math.min(35, elapsed));
            const dist = Math.hypot(rawDx, rawDy);
            gain = getGain(dist / dt);
            pt.history = [
              { t: now - dt, x: pt.lastX - rawDx, y: pt.lastY - rawDy },
              { t: now, x: e.clientX, y: e.clientY },
            ];
          } else {
            pt.history.push({ t: now, x: e.clientX, y: e.clientY });
            while (pt.history.length > 2 && (now - pt.history[0].t) > 45) {
              pt.history.shift();
            }
            const oldest = pt.history[0];
            const dt = now - oldest.t;
            if (dt > 0) {
              const dist = Math.hypot(e.clientX - oldest.x, e.clientY - oldest.y);
              gain = getGain(dist / dt);
            } else {
              gain = getGain(0);
            }
          }
          pt.lastMoveTime = now;
        }
        queueMove(rawDx * gain * sensitivity, rawDy * gain * sensitivity);
      } else if (activePointers.size === 2) {
        if (isDragging) {
          isDragging = false;
          send({ type: 'mouseup' });
        }
        const factorY = natural ? 1 : -1;
        const factorX = natural ? -1 : 1;
        // Two pointers concurrently fire pointermove; scale by 5 so total 2-finger sensitivity matches 1-finger scrollbar
        const scale = scrollSpeed * 5;
        const dxDelta = rawDx * scale * factorX;
        const dyDelta = rawDy * scale * factorY;

        cumScrollX += Math.abs(rawDx);
        cumScrollY += Math.abs(rawDy);

        if (!scrollAxis) {
          if (cumScrollX + cumScrollY > 3) {
            moved = true;
            if (cumScrollY >= cumScrollX * 1.25) scrollAxis = 'y';
            else if (cumScrollX >= cumScrollY * 1.25) scrollAxis = 'x';
            else scrollAxis = 'free';
          }
        } else if (scrollAxis === 'y') {
          if (Math.abs(rawDx) > Math.abs(rawDy) * 2.5 && Math.abs(rawDx) > 7) {
            scrollAxis = 'x';
            cumScrollX = Math.abs(rawDx);
            cumScrollY = 0;
          }
        } else if (scrollAxis === 'x') {
          if (Math.abs(rawDy) > Math.abs(rawDx) * 2.5 && Math.abs(rawDy) > 7) {
            scrollAxis = 'y';
            cumScrollY = Math.abs(rawDy);
            cumScrollX = 0;
          }
        }

        if (scrollAxis === 'y') {
          queueScroll(0, dyDelta);
        } else if (scrollAxis === 'x') {
          queueScroll(dxDelta, 0);
        } else {
          const sendX = Math.abs(rawDx) >= 1.5 ? dxDelta : 0;
          const sendY = Math.abs(rawDy) >= 1.5 ? dyDelta : 0;
          queueScroll(sendX, sendY);
        }
      }
    });

    const onPointerEnd = (e) => {
      e.stopPropagation(); e.preventDefault();
      try { pad.releasePointerCapture(e.pointerId); } catch (_) {}
      if (holdTimer) { clearTimeout(holdTimer); holdTimer = null; }
      // a quick flick can lift in the frame of its last move: judge the fingers now, before the one that lifts is gone
      if (e.type === 'pointerup' && gestureBase && !gestureFired) checkGesture(true);
      const pt = activePointers.get(e.pointerId);
      if (pt?.isBar) {
        if (scrollBarEl) scrollBarEl.classList.remove('is-scrolling');
        flushScroll();
      } else if (isDragging) {
        flushMove();
        send({ type: 'mouseup' });
        isDragging = false;
        tapFired = true;
      } else if (!moved && !tapFired && e.type !== 'pointercancel') {
        const duration = pt ? performance.now() - pt.downTime : 9999;
        if (maxActivePointers === 1 && duration < 300) {
          flushMove();
          send({ type: 'click' });
          feel('click');
          tapFired = true;
        } else if (maxActivePointers === 2 && duration < 400) {
          flushMove();
          flushScroll();
          send({ type: 'rightClick' });
          feel('rightClick');
          tapFired = true;
        }
      }
      activePointers.delete(e.pointerId);
      rebase();
      if (activePointers.size < 2) {
        scrollAxis = null;
        cumScrollX = 0;
        cumScrollY = 0;
      }
      if (activePointers.size === 0) {
        maxActivePointers = 0; moved = false; tapFired = false;
        gestureFired = false; gestureBase = null; scrollSince = 0;
        if (scrollBarEl) scrollBarEl.classList.remove('is-scrolling');
        flushMove(); flushScroll();
      }
    };

    pad.addEventListener('pointerup', onPointerEnd);
    pad.addEventListener('pointercancel', onPointerEnd);
    pad.addEventListener('touchstart', (e) => { e.stopPropagation(); }, { passive: false });
    pad.addEventListener('touchmove', (e) => { e.stopPropagation(); e.preventDefault(); }, { passive: false });
    pad.addEventListener('touchend', (e) => { e.stopPropagation(); }, { passive: false });
    pad.addEventListener('contextmenu', (e) => { e.preventDefault(); e.stopPropagation(); });

    return {
      update() {},
      destroy() {
        destroyed = true;
        moveScheduled = false;
        scrollScheduled = false;
        accScrollX = 0;
        accScrollY = 0;
        scrollAxis = null;
        cumScrollX = 0;
        cumScrollY = 0;
        if (reconnectTimer) clearTimeout(reconnectTimer);
        if (holdTimer) clearTimeout(holdTimer);
        clearTimeout(gestureQuietTimer);
        if (isDragging) { send({ type: 'mouseup' }); isDragging = false; }
        if (ws) { try { ws.close(); } catch (_) {} ws = null; }
        activePointers.clear();
      }
    };
  }
};

let screenSessionQueue = Promise.resolve();
const screenSessionCommand = (path, query = '') => {
  screenSessionQueue = screenSessionQueue.catch(() => {}).then(() => api(path + query, 'POST', 10000));
  return screenSessionQueue;
};

W['screen-mirror'] = {
  needs: ['tick'],
  always: true,
  create(box, { page, o = {} }) {
    box.classList.add('screen-mirror-widget');
    const streamEnabled = window.parent === window;
    const message = h('div', 'screen-mirror-empty', '');
    const stage = h('div', 'screen-mirror-stage', message);
    stage.setAttribute('aria-label', 'Màn hình phụ');
    const video = h('video', 'screen-mirror-frame');
    video.autoplay = true;
    video.muted = true;
    video.playsInline = true;
    video.disablePictureInPicture = true;
    const canvas = h('canvas', 'screen-mirror-frame');
    const ctx = canvas.getContext('2d', { alpha: false, desynchronized: true });
    box.append(stage);

    let active = false, starting = false, destroyed = false, current = false, wasCurrent = false;
    let streamAbort = null, mediaSource = null, mediaSourceUrl = null, sourceBuffer = null, videoDecoder = null;
    let nativeActive = false, nativeHandler = null, resizeObserver = null;
    let lastResolution = '', lastStartTry = 0, lastRestartAt = 0;
    const hasNativeMirror = streamEnabled && typeof bridge?.startScreenMirror === 'function';
    const computeNativeRect = () => {
      if (!active || !current || destroyed || nightOn || sheet) return null;
      const ov = document.getElementById('overview');
      const ap = document.getElementById('approval');
      if ((ov && !ov.hidden) || (ap && !ap.hidden)) return null;
      const r = stage.getBoundingClientRect();
      if (r.width < 16 || r.height < 16 || r.right <= 0 || r.left >= window.innerWidth) return null;
      const dpr = window.devicePixelRatio || 1;
      const left = Math.max(0, Math.round(r.left * dpr));
      const top = Math.max(0, Math.round(r.top * dpr));
      const right = Math.min(Math.round(window.innerWidth * dpr), Math.round(r.right * dpr));
      const bottom = Math.min(Math.round(window.innerHeight * dpr), Math.round(r.bottom * dpr));
      const width = right - left;
      const height = bottom - top;
      return width >= 16 && height >= 16 ? { left, top, width, height } : null;
    };
    const syncNativeRect = () => {
      if (!nativeActive || !hasNativeMirror) return;
      const rect = computeNativeRect();
      if (rect) bridge.updateScreenMirrorRect(rect.left, rect.top, rect.width, rect.height);
      else bridge.updateScreenMirrorRect(0, 0, 0, 0);
    };
    const fallbackPointers = new Map();
    const touchQueue = [];
    let touchInFlight = false;
    const isMoveOnlyContacts = (contacts) =>
      contacts.length > 0 && contacts.every(c => c.state === 1);
    const pumpFallbackTouch = () => {
      if (touchInFlight || touchQueue.length === 0) return;
      const contacts = touchQueue.shift();
      touchInFlight = true;
      fetch(`${API}/screen/touch`, {
        method: 'POST',
        headers: { ...PANEL_HEADERS, 'Content-Type': 'application/json' },
        body: JSON.stringify({ contacts }),
        keepalive: true,
      }).catch(() => {}).finally(() => {
        touchInFlight = false;
        pumpFallbackTouch();
      });
    };
    const flushFallbackTouch = (contacts) => {
      if (touchQueue.length > 0 && isMoveOnlyContacts(contacts) && isMoveOnlyContacts(touchQueue[touchQueue.length - 1])) {
        touchQueue[touchQueue.length - 1] = contacts;
      } else {
        if (touchQueue.length >= 32) {
          const moveIdx = touchQueue.findIndex(isMoveOnlyContacts);
          touchQueue.splice(moveIdx >= 0 ? moveIdx : 0, 1);
        }
        touchQueue.push(contacts);
      }
      pumpFallbackTouch();
    };
    const fallbackContentBox = () => {
      const r = stage.getBoundingClientRect();
      const mw = video.parentNode === stage && video.videoWidth > 0 ? video.videoWidth
        : (canvas.parentNode === stage && canvas.width > 0 ? canvas.width : r.width);
      const mh = video.parentNode === stage && video.videoHeight > 0 ? video.videoHeight
        : (canvas.parentNode === stage && canvas.height > 0 ? canvas.height : r.height);
      if (r.width <= 0 || r.height <= 0 || mw <= 0 || mh <= 0) return null;
      const scale = Math.min(r.width / mw, r.height / mh);
      const dw = mw * scale, dh = mh * scale;
      return { left: r.left + (r.width - dw) / 2, top: r.top + (r.height - dh) / 2, width: dw, height: dh };
    };
    const handleFallbackPointer = (e, state) => {
      if (nativeActive || !active || !current || destroyed || !streamEnabled) return;
      if (video.parentNode !== stage && canvas.parentNode !== stage) return;
      const boxRect = fallbackContentBox();
      if (!boxRect || boxRect.width <= 0 || boxRect.height <= 0) return;
      e.preventDefault();
      if (state === 0) {
        try { stage.setPointerCapture?.(e.pointerId); } catch (_) {}
      }
      const x = Math.max(0, Math.min(1, (e.clientX - boxRect.left) / boxRect.width));
      const y = Math.max(0, Math.min(1, (e.clientY - boxRect.top) / boxRect.height));
      const pressure = e.pressure > 0 ? Math.max(1, Math.min(1024, Math.round(e.pressure * 1024))) : 512;
      const id = Math.abs(e.pointerId | 0) % 256;
      for (const [pid, c] of fallbackPointers) {
        if (pid !== e.pointerId && c.state === 0) c.state = 1;
      }
      fallbackPointers.set(e.pointerId, { id, state, x, y, pressure });
      const contacts = [...fallbackPointers.values()].map(c => ({ ...c }));
      if (state === 2 || state === 3) fallbackPointers.delete(e.pointerId);
      flushFallbackTouch(contacts);
    };
    stage.addEventListener('pointerdown', (e) => handleFallbackPointer(e, 0));
    stage.addEventListener('pointermove', (e) => { if (fallbackPointers.has(e.pointerId)) handleFallbackPointer(e, 1); });
    stage.addEventListener('pointerup', (e) => { if (fallbackPointers.has(e.pointerId)) handleFallbackPointer(e, 2); });
    stage.addEventListener('pointercancel', (e) => { if (fallbackPointers.has(e.pointerId)) handleFallbackPointer(e, 3); });
    const discardStream = () => {
      if (fallbackPointers.size > 0) {
        fallbackPointers.clear();
        touchQueue.length = 0;
        flushFallbackTouch([]);
      }
      if (nativeActive) {
        nativeActive = false;
        try { bridge?.stopScreenMirror?.(); } catch (_) {}
      }
      if (nativeHandler && window.onNativeScreenMirror === nativeHandler) window.onNativeScreenMirror = null;
      nativeHandler = null;
      resizeObserver?.disconnect();
      resizeObserver = null;
      streamAbort?.abort(); streamAbort = null;
      if (videoDecoder) {
        try { if (videoDecoder.state !== 'closed') videoDecoder.close(); } catch (_) {}
        videoDecoder = null;
      }
      if (sourceBuffer && mediaSource?.readyState === 'open') {
        try { mediaSource.removeSourceBuffer(sourceBuffer); } catch (_) {}
      }
      if (mediaSource?.readyState === 'open') {
        try { mediaSource.endOfStream(); } catch (_) {}
      }
      sourceBuffer = null; mediaSource = null;
      if (mediaSourceUrl) URL.revokeObjectURL(mediaSourceUrl);
      mediaSourceUrl = null;
      video.pause(); video.removeAttribute('src'); video.load();
    };
    const failStream = async detail => {
      if (!active || !current || destroyed) return;
      const mediaError = video.error;
      const mediaDetails = mediaError ? ` (media ${mediaError.code}, ${mediaError.message || ''})` : '';
      active = false;
      discardStream();
      stage.replaceChildren(message);
      message.textContent = `Không nhận được luồng H.264 từ NVIDIA: ${detail || 'không thể phát'}${mediaDetails}`;
      try { await screenSessionCommand('/screen/stop'); } catch (_) {}
    };
    const appendChunk = (buffer, bytes) => new Promise((resolve, reject) => {
      const done = () => { cleanup(); resolve(); };
      const failed = () => { cleanup(); reject(new Error('MediaSource không giải mã được phân đoạn MP4/H.264')); };
      const cleanup = () => { buffer.removeEventListener('updateend', done); buffer.removeEventListener('error', failed); };
      buffer.addEventListener('updateend', done, { once: true });
      buffer.addEventListener('error', failed, { once: true });
      try { buffer.appendBuffer(bytes); } catch (error) { cleanup(); reject(error); }
    });
    const removeOldBuffer = buffer => {
      const start = buffer.buffered.length ? buffer.buffered.start(0) : 0;
      const end = video.currentTime - 2;
      if (end <= start + 0.05) return Promise.resolve();
      return new Promise((resolve, reject) => {
      const done = () => { cleanup(); resolve(); };
      const failed = () => { cleanup(); reject(new Error('MediaSource không thể dọn bộ đệm cũ')); };
      const cleanup = () => { buffer.removeEventListener('updateend', done); buffer.removeEventListener('error', failed); };
      buffer.addEventListener('updateend', done, { once: true });
      buffer.addEventListener('error', failed, { once: true });
      try { buffer.remove(start, end); } catch (error) { cleanup(); reject(error); }
      });
    };
    const findAud = (buf, from = 0) => {
      for (let i = from; i <= buf.length - 5; i++) {
        if (buf[i] === 0 && buf[i + 1] === 0 && buf[i + 2] === 0 && buf[i + 3] === 1 && buf[i + 4] === 9) return i;
      }
      return -1;
    };
    const isIdrAccessUnit = au => {
      for (let i = 0; i <= au.length - 4; i++) {
        if (au[i] === 0 && au[i + 1] === 0) {
          if (au[i + 2] === 1 && (au[i + 3] & 0x1f) === 5) return true;
          if (i <= au.length - 5 && au[i + 2] === 0 && au[i + 3] === 1 && (au[i + 4] & 0x1f) === 5) return true;
        }
      }
      return false;
    };
    const connectWebCodecs = async () => {
      if (!active || !current || destroyed) return false;
      if (typeof window.VideoDecoder !== 'function' || typeof window.EncodedVideoChunk !== 'function' || !ctx) return false;
      const config = { codec: 'avc1.4D402A', optimizeForLatency: true, hardwareAcceleration: 'prefer-hardware' };
      try {
        const support = await VideoDecoder.isConfigSupported(config);
        if (!support.supported) return false;
      } catch (_) { return false; }
      if (!active || !current || destroyed) return true;
      stage.replaceChildren(canvas);
      streamAbort = new AbortController();
      const abort = streamAbort;
      let sawKey = false, frameIndex = 0;
      const decoder = new VideoDecoder({
        output(frame) {
          if (canvas.width !== frame.displayWidth || canvas.height !== frame.displayHeight) {
            canvas.width = frame.displayWidth;
            canvas.height = frame.displayHeight;
          }
          ctx.drawImage(frame, 0, 0);
          frame.close();
        },
        error(err) {
          if (!abort.signal.aborted) failStream(err?.message || 'lỗi VideoDecoder');
        },
      });
      videoDecoder = decoder;
      decoder.configure(config);
      (async () => {
        try {
          const response = await fetch(`${API}/screen/h264?format=annexb&t=${Date.now()}`, {
            headers: PANEL_HEADERS, cache: 'no-store', signal: abort.signal,
          });
          if (!response.ok) throw new Error(`Agent trả HTTP ${response.status}`);
          if (!response.body) throw new Error('WebView không hỗ trợ đọc luồng dữ liệu');
          const reader = response.body.getReader();
          let pending = new Uint8Array(0);
          while (active && current && !destroyed && decoder.state === 'configured') {
            const { value, done } = await reader.read();
            if (done) throw new Error('Agent đã đóng luồng');
            if (!value?.byteLength) continue;
            if (pending.length === 0) pending = value;
            else {
              const merged = new Uint8Array(pending.length + value.length);
              merged.set(pending, 0);
              merged.set(value, pending.length);
              pending = merged;
            }
            const firstAud = findAud(pending, 0);
            if (firstAud < 0) {
              if (pending.length > 512 * 1024) pending = new Uint8Array(0);
              continue;
            }
            if (firstAud > 0) pending = pending.subarray(firstAud);
            let nextAud;
            while ((nextAud = findAud(pending, 5)) >= 0) {
              const au = pending.slice(0, nextAud);
              pending = pending.subarray(nextAud);
              const isKey = isIdrAccessUnit(au);
              if (!sawKey) {
                if (!isKey) continue;
                sawKey = true;
              }
              if (decoder.decodeQueueSize > 3 && !isKey) { sawKey = false; continue; }
              decoder.decode(new EncodedVideoChunk({
                type: isKey ? 'key' : 'delta',
                timestamp: frameIndex++ * 16667,
                data: au,
              }));
            }
          }
          try { await reader.cancel(); } catch (_) {}
        } catch (error) {
          if (!abort.signal.aborted) failStream(error?.message || 'lỗi WebCodecs');
        }
      })();
      return true;
    };
    const connectNative = () => {
      if (!hasNativeMirror || !active || !current || destroyed) return false;
      stage.replaceChildren(message);
      message.textContent = '';
      nativeActive = true;
      const handler = (event, detail) => {
        if (nativeHandler !== handler || !active || !current || destroyed) return;
        if (event === 'connected') {
          message.textContent = '';
          syncNativeRect();
        } else if (event === 'reconnecting') {
          message.textContent = 'Đang kết nối lại màn hình phụ...';
        } else if (event === 'restarting') {
          message.textContent = 'Đang khởi động lại màn hình phụ...';
          if (Date.now() - lastRestartAt > 2000) {
            lastRestartAt = Date.now();
            screenSessionCommand('/screen/start', lastResolution).catch(() => {});
          }
        } else if (event === 'fallback') {
          nativeActive = false;
          if (window.onNativeScreenMirror === handler) window.onNativeScreenMirror = null;
          nativeHandler = null;
          resizeObserver?.disconnect();
          resizeObserver = null;
          connectStream();
        } else if (event === 'error') {
          nativeActive = false;
          failStream(detail || 'lỗi phát native MediaCodec');
        }
      };
      nativeHandler = handler;
      window.onNativeScreenMirror = handler;
      const rect = computeNativeRect() ?? {
        left: 0,
        top: 0,
        width: Math.max(16, Math.round((window.innerWidth || 1280) * (window.devicePixelRatio || 1))),
        height: Math.max(16, Math.round((window.innerHeight || 720) * (window.devicePixelRatio || 1))),
      };
      if (typeof ResizeObserver === 'function') {
        resizeObserver = new ResizeObserver(() => syncNativeRect());
        resizeObserver.observe(stage);
      }
      const audioMode = typeof o.audio === 'string' ? o.audio : (o.audio === false ? 'off' : 'default');
      const audioDevice = typeof o.audioDevice === 'string' ? o.audioDevice : '';
      const audioAutoSwitch = !!o.audioAutoSwitch;
      const fitMode = typeof o.fit === 'string' ? o.fit : (o.__primary ? 'contain' : 'fill');
      const targetFps = Math.max(15, Math.min(60, parseInt(o.fps, 10) || (o.__primary ? 30 : 60)));
      bridge.startScreenMirror(rect.left, rect.top, rect.width, rect.height, !!o.hevc, audioMode, audioDevice, audioAutoSwitch, fitMode, targetFps);
      return true;
    };
    const connectStream = async () => {
      if (!active || !current || destroyed) return;
      if (await connectWebCodecs()) return;
      if (!active || !current || destroyed) return;
      stage.replaceChildren(video);
      const mime = 'video/mp4; codecs="avc1.4D402A"';
      if (!window.MediaSource || !MediaSource.isTypeSupported(mime)) {
        failStream(`WebView không hỗ trợ MediaSource ${mime}`);
        return;
      }
      mediaSource = new MediaSource();
      mediaSourceUrl = URL.createObjectURL(mediaSource);
      video.src = mediaSourceUrl;
      video.onerror = () => failStream(`video.error=${video.error?.code ?? 0}, network=${video.networkState}`);
      streamAbort = new AbortController();
      const abort = streamAbort;
      mediaSource.addEventListener('sourceopen', async () => {
        if (abort.signal.aborted || !active || !current || destroyed) return;
        try {
          sourceBuffer = mediaSource.addSourceBuffer(mime);
          sourceBuffer.mode = 'segments';
          const response = await fetch(`${API}/screen/h264?t=${Date.now()}`, {
            headers: PANEL_HEADERS, cache: 'no-store', signal: abort.signal,
          });
          if (!response.ok) throw new Error(`Agent trả HTTP ${response.status}`);
          if (!response.body) throw new Error('WebView không hỗ trợ đọc luồng dữ liệu');
          const reader = response.body.getReader();
          let firstChunk = true, lastCleanup = 0;
          while (active && current && !destroyed) {
            const { value, done } = await reader.read();
            if (done) throw new Error('Agent đã đóng luồng');
            if (!value?.byteLength) continue;
            await appendChunk(sourceBuffer, value);
            if (firstChunk) {
              firstChunk = false;
              video.play().catch(error => failStream(error?.message || 'không thể bắt đầu phát'));
            }
            const now = performance.now();
            if (sourceBuffer.buffered.length && video.readyState >= 2 && !sourceBuffer.updating) {
              const start = sourceBuffer.buffered.start(0);
              const end = sourceBuffer.buffered.end(sourceBuffer.buffered.length - 1);
              const lag = end - video.currentTime;
              // Keep the phone near the live edge instead of letting MSE's forward buffer add delay.
              if (lag > 0.08) {
                try { video.currentTime = Math.max(start, end - 0.01); } catch (_) {}
                video.playbackRate = 1;
              } else if (lag > 0.03) {
                video.playbackRate = 1.1;
              } else if (video.playbackRate !== 1) {
                video.playbackRate = 1;
              }
              if (now - lastCleanup > 1000 && video.currentTime > start + 2.1) {
                lastCleanup = now;
                await removeOldBuffer(sourceBuffer);
              }
            }
          }
          try { await reader.cancel(); } catch (_) {}
        } catch (error) {
          if (!abort.signal.aborted) failStream(error?.message || 'lỗi MediaSource');
        }
      }, { once: true });
    };
    const startSession = async () => {
      if (!streamEnabled || active || starting || destroyed) return;
      starting = true; message.textContent = '';
      lastStartTry = Date.now();
      try {
        let resolution = '';
        if (o.__primary) {
          resolution = '?display=primary';
        } else {
        try {
          const size = JSON.parse(bridge?.screenSize?.() || '{}');
          if (Number.isInteger(size.width) && Number.isInteger(size.height) && size.width >= 320 && size.height >= 200) {
            let w = size.width, h = size.height;
            const targetShort = { sd: 480, hd: 720, fhd: 1080, '2k': 1440, '4k': 2160 }[o.quality];
            if (targetShort) {
              const shortSide = Math.min(w, h), longSide = Math.max(w, h);
              const scaledLong = Math.round((targetShort * longSide / shortSide) / 2) * 2;
              if (w >= h) { w = scaledLong; h = targetShort; }
              else { w = targetShort; h = scaledLong; }
            }
            resolution = `?width=${w}&height=${h}`;
          }
        } catch (_) {}
        }
        lastResolution = resolution;
        const status = await screenSessionCommand('/screen/start', resolution);
        if (!status.supported) throw new Error('Cần PhoneBox Agent trên Windows.');
        if (status.error) throw new Error(status.error);
        if (!status.displayAvailable) {
          message.textContent = status.error || 'Chưa có màn hình phụ trên card NVIDIA';
          return;
        }
        if (destroyed || !current) { await screenSessionCommand('/screen/stop'); return; }
        active = true;
        stage.replaceChildren(video);
        if (!connectNative()) connectStream();
      } catch (error) {
        message.textContent = error?.message || 'Không kết nối được với PC';
      } finally { starting = false; }
    };
    const stopSession = async () => {
      if (!streamEnabled) return;
      active = false; discardStream();
      message.textContent = '';
      stage.replaceChildren(message);
      try { await screenSessionCommand('/screen/stop'); } catch (_) {}
    };
    if (!streamEnabled) message.textContent = 'Mở trang này trên điện thoại để bắt đầu truyền màn hình.';
    else stage.onclick = () => { if (current && !active && !starting && !destroyed) startSession(); };
    const syncPage = () => {
      current = pages[curPage] === page;
      if (current && !wasCurrent) startSession();
      else if (!current && wasCurrent) stopSession();
      else if (current && !active && !starting && !destroyed && streamEnabled && !document.body.classList.contains('offline') && Date.now() - lastStartTry > 4000) startSession();
      else if (current && nativeActive) syncNativeRect();
      wasCurrent = current;
    };
    const onAppPause = (event) => {
      if (event.detail) stopSession();
      else if (current) startSession();
    };
    window.addEventListener('phonebox-app-pause', onAppPause);
    window.addEventListener('phonebox-page-change', syncPage);
    return {
      update(kind) {
        if (kind === 'tick') syncPage();
      },
      destroy() {
        destroyed = true; current = false;
        window.removeEventListener('phonebox-app-pause', onAppPause);
        window.removeEventListener('phonebox-page-change', syncPage);
        stopSession();
      },
    };
  },
};

W['primary-mirror'] = {
  needs: ['tick'],
  always: true,
  create(box, ctx) {
    return W['screen-mirror'].create(box, { ...ctx, o: { ...(ctx.o || {}), __primary: true } });
  },
};
