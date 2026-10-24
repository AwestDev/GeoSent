// Excel coordinate layers (Phase 2): a marker (halo + dot + label, or an icon marker) and a radius circle for every point,
// drawn on top of the opened map. Independent of the tile layers in map.html; map.html only calls coordAttach(map, dark)
// when it creates a map and forwards "coord*" host messages to coordHandle(m).
//
// The host sends ready-made GeoJSON: points, and radius circles as polygons whose vertices are real WGS84 positions
// computed in C# (GeoMath.CircleRing). This page never converts metres, so circles stay metre-accurate at every
// latitude and zoom (MapLibre projects the polygon to Web Mercator). Layers are cached here, so they are redrawn
// when a different map file is opened and the map is rebuilt.
//
// Marker style: the host sends a colour (#RRGGBB), optionally an icon as SVG path data (24x24 grid, even-odd), and a
// shape: circle (Excel layers), pin ("my points") or diamond (Explorer/Monitoring results). With an icon the page draws the
// shape in the colour with the white glyph (canvas + Path2D) and shows it as a symbol layer; an almost transparent circle
// layer stays underneath so hover and click keep working. Without an icon: halo + dot as before.
// A style change arrives as a normal "coordUpsert"; the layer is rebuilt when its colour/icon/shape differs from what is drawn.
//
// Interaction: hovering a point shows a tooltip with its name (and posts "pointHover" so the host can show it in the
// status bar); clicking a point highlights it, posts "pointSelect" and fires a window event "coordselect" (used by
// map-tools.js); clicking empty map clears the highlight ("coordselectclear").
(function () {
  'use strict';

  const FONT = ['NotoSans'];
  const PARTS = ['fill', 'line', 'halo', 'dot', 'icon', 'label'];
  const SEL_SRC = 'coord|selected|src', SEL_HALO = 'coord|selected|halo', SEL_RING = 'coord|selected|ring';
  const HIT_RADIUS = 9; // px around the cursor in which a point counts as hit

  const host = window.chrome && window.chrome.webview;
  const post = (m) => host && host.postMessage(m);

  const layers = new Map();      // layer id -> last "coordUpsert" message (name/visible kept up to date)
  const drawn = new Map();       // layer id -> style signature (colour|iconPath|shape) currently on the map
  let cmap = null, ready = false, dark = true;
  let pendingFit = null;
  let selected = null;           // { layerId, pointId } of the highlighted point
  let hovered = null;            // "layerId|pointId" of the point under the mouse
  let tip = null;
  let moveEvent = null, moveQueued = false;

  const lid = (id, part) => 'coord|' + id + '|' + part;
  const sid = (id, part) => 'coord|' + id + '|src-' + part;
  const imgName = (id) => 'coord|' + id + '|marker';
  const hue = (s) => { let h = 0; for (const c of s) h = (h * 31 + c.charCodeAt(0)) >>> 0; return h % 360; };
  const colorOf = (id) => `hsl(${hue(id)}, 70%, ${dark ? 62 : 42}%)`; // fallback only: the host normally sends a colour
  const accent = () => (dark ? '#8A90FF' : '#4F46E5');
  const empty = () => ({ type: 'FeatureCollection', features: [] });
  const clipText = (value, limit) => {
    const text = String(value);
    if (text.length <= limit) return text;
    let end = Math.max(0, limit - 1);
    if (end > 0 && text.charCodeAt(end - 1) >= 0xD800 && text.charCodeAt(end - 1) <= 0xDBFF
        && text.charCodeAt(end) >= 0xDC00 && text.charCodeAt(end) <= 0xDFFF) end--;
    return text.slice(0, end) + '…';
  };

  // ---- tooltip ------------------------------------------------------------------------------------------------

  function ensureTip() {
    if (tip) return tip;
    const style = document.createElement('style');
    style.textContent =
      '#coord-tip{position:absolute;z-index:5;pointer-events:auto;display:none;box-sizing:border-box;' +
      'width:max-content;max-width:min(360px,calc(100vw - 16px));max-height:calc(100vh - 16px);' +
      'overflow:auto;overflow-wrap:anywhere;white-space:normal;padding:7px 10px;' +
      'border-radius:8px;font:600 12px "Segoe UI",Tahoma,sans-serif;box-shadow:0 4px 14px rgba(0,0,0,.28);' +
      '}' +
      '#coord-tip small{display:block;font-weight:400;font-size:11px;opacity:.7;margin-top:2px}' +
      '#coord-tip .coord-detail{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,2fr);gap:2px 8px;' +
      'margin-top:5px;font-weight:400;line-height:1.35}' +
      '#coord-tip .coord-key{opacity:.68}#coord-tip .coord-value{min-width:0;white-space:pre-wrap}';
    document.head.appendChild(style);
    tip = document.createElement('div');
    tip.id = 'coord-tip';
    tip.addEventListener('mouseleave', (e) => {
      const next = e.relatedTarget;
      if (cmap && next && cmap.getCanvas().contains(next)) return;
      clearHover();
    });
    document.body.appendChild(tip);
    return tip;
  }

  // The details of a point (an object, or its JSON text) as a small two-column list; null when there is nothing to show.
  function detailList(details) {
    if (typeof details === 'string') {
      try { details = JSON.parse(details); } catch (_) { details = null; }
    }
    if (!details || typeof details !== 'object' || Array.isArray(details)) return null;
    const entries = Object.entries(details).slice(0, 8);
    if (!entries.length) return null;
    const list = document.createElement('div');
    list.className = 'coord-detail';
    let remaining = 1200;
    for (const [key, value] of entries) {
      if (remaining <= 0) break;
      const keyText = clipText(key, Math.min(80, remaining));
      remaining -= keyText.length;
      if (remaining <= 0) break;
      const valueText = clipText(value, Math.min(240, remaining));
      remaining -= valueText.length;
      const k = document.createElement('span');
      k.className = 'coord-key';
      k.dir = 'auto';
      k.textContent = keyText;
      const v = document.createElement('span');
      v.className = 'coord-value';
      v.dir = 'auto';
      v.textContent = valueText;
      list.append(k, v);
    }
    return list.childElementCount ? list : null;
  }

  // ---- selected point card ------------------------------------------------------------------------------------------
  // A clicked point shows its details here until its close button is pressed, another point is clicked, or the empty
  // map is clicked. Unlike the hover tip it does not go away when the pointer leaves the map, so it stays open while the
  // user presses a button of the program, and it is part of the page when the map is saved as a picture.
  let card = null, cardBody = null;

  function ensureCard() {
    if (card) return card;
    const style = document.createElement('style');
    style.textContent =
      '#coord-card .coord-detail{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,2fr);gap:2px 8px;margin-top:6px}' +
      '#coord-card .coord-key{opacity:.68}#coord-card .coord-value{min-width:0;white-space:pre-wrap}';
    document.head.appendChild(style);
    card = document.createElement('div');
    card.id = 'coord-card';
    card.style.cssText = 'position:absolute;left:12px;bottom:12px;z-index:6;box-sizing:border-box;display:none;' +
      'width:min(320px,calc(100% - 24px));max-height:50%;overflow:auto;overflow-wrap:anywhere;' +
      'padding:10px 34px 10px 12px;border-radius:10px;font:12px/1.4 "Segoe UI",Tahoma,sans-serif;' +
      'box-shadow:0 6px 20px rgba(0,0,0,.22);';
    const close = document.createElement('button');
    close.type = 'button';
    close.textContent = '\u00D7';
    close.title = 'بستن';
    close.style.cssText = 'position:absolute;top:4px;right:6px;border:0;background:transparent;color:inherit;' +
      'font-size:18px;line-height:1;cursor:pointer;';
    close.addEventListener('click', (e) => { e.stopPropagation(); clearSelection(true); });
    cardBody = document.createElement('div');
    card.append(close, cardBody);
    document.body.appendChild(card);
    return card;
  }

  function showCard(name, layerName, lat, lng, details) {
    const c = ensureCard();
    c.style.background = dark ? '#1F2540' : '#FFFFFF';
    c.style.color = dark ? '#E8EBF5' : '#151B2C';
    c.style.border = '1px solid ' + (dark ? '#2B3252' : '#E0E5EE');
    cardBody.textContent = '';
    const title = document.createElement('div');
    title.dir = 'auto';
    title.style.fontWeight = '600';
    title.textContent = name;
    cardBody.appendChild(title);
    if (layerName) {
      const layer = document.createElement('div');
      layer.dir = 'auto';
      layer.style.cssText = 'font-size:11px;opacity:.7';
      layer.textContent = layerName;
      cardBody.appendChild(layer);
    }
    const coords = document.createElement('div');
    coords.dir = 'ltr';
    coords.style.cssText = 'font-size:11px;opacity:.7';
    coords.textContent = lat.toFixed(6) + ', ' + lng.toFixed(6);
    cardBody.appendChild(coords);
    const list = detailList(details);
    if (list) cardBody.appendChild(list);
    c.style.display = 'block';
  }

  function hideCard() { if (card) card.style.display = 'none'; }

  function showTip(point, name, layerName, details) {
    const t = ensureTip();
    t.style.background = dark ? '#1F2540' : '#FFFFFF';
    t.style.color = dark ? '#E8EBF5' : '#151B2C';
    t.style.border = '1px solid ' + (dark ? '#2B3252' : '#E0E5EE');
    t.textContent = '';
    const n = document.createElement('div');
    n.dir = 'auto';
    n.textContent = name;
    t.appendChild(n);
    if (layerName) {
      const s = document.createElement('small');
      s.dir = 'auto';
      s.textContent = layerName;
      t.appendChild(s);
    }
    const detailsBox = detailList(details);
    if (detailsBox) t.appendChild(detailsBox);
    t.style.display = 'block';
    const w = t.offsetWidth, h = t.offsetHeight;
    let x = point.x + 14, y = point.y + 14;
    if (x + w > window.innerWidth - 4) x = point.x - w - 14;
    if (y + h > window.innerHeight - 4) y = point.y - h - 14;
    t.style.left = Math.max(4, Math.min(x, window.innerWidth - w - 4)) + 'px';
    t.style.top = Math.max(4, Math.min(y, window.innerHeight - h - 4)) + 'px';
  }

  function hideTip() { if (tip) tip.style.display = 'none'; }

  // ---- drawing --------------------------------------------------------------------------------------------------

  function setVisible(id, visible) {
    for (const p of PARTS)
      if (cmap.getLayer(lid(id, p))) cmap.setLayoutProperty(lid(id, p), 'visibility', visible ? 'visible' : 'none');
  }

  // A marker image: circle (default), pin (tip at the bottom), diamond or rounded square, in the colour, with a white ring
  // and the white glyph. Drawn at 64 px width and shown at pixelRatio 2 (= 32 css px wide); the pin is 84 px high.
  function makeMarker(color, pathData, shape) {
    const W = 64, H = shape === 'pin' ? 84 : 64;
    const canvas = document.createElement('canvas');
    canvas.width = W; canvas.height = H;
    const g = canvas.getContext('2d');
    const cx = W / 2, cy = shape === 'pin' ? 30 : 32;

    g.beginPath();
    if (shape === 'pin') {
      g.moveTo(cx, 80);
      g.bezierCurveTo(cx - 8, 66, cx - 25, 52, cx - 25, cy);
      g.arc(cx, cy, 25, Math.PI, 0, false);
      g.bezierCurveTo(cx + 25, 52, cx + 8, 66, cx, 80);
      g.closePath();
    } else if (shape === 'diamond') {
      g.save(); g.translate(cx, cy); g.rotate(Math.PI / 4); g.roundRect(-19, -19, 38, 38, 8); g.restore();
    } else if (shape === 'square') {
      g.roundRect(cx - 24, cy - 24, 48, 48, 12);
    } else {
      g.arc(cx, cy, W / 2 - 4, 0, Math.PI * 2);
    }
    g.shadowColor = 'rgba(0,0,0,0.35)'; g.shadowBlur = 5; g.shadowOffsetY = 1.5;
    g.fillStyle = color; g.fill();
    g.shadowColor = 'transparent';
    g.lineWidth = 3; g.strokeStyle = '#FFFFFF'; g.stroke();

    const glyph = shape === 'pin' ? 26 : (shape === 'diamond' ? 22 : 30), scale = glyph / 24;
    g.save();
    g.translate(cx - glyph / 2, cy - glyph / 2);
    g.scale(scale, scale);
    g.fillStyle = '#FFFFFF';
    g.fill(new Path2D(pathData), 'evenodd');
    g.restore();
    return g.getImageData(0, 0, W, H);
  }

  function draw(l) {
    if (!cmap || !ready) return;
    const pointsId = sid(l.id, 'points'), circlesId = sid(l.id, 'circles');
    const style = (l.color || '') + '|' + (l.iconPath || '') + '|' + (l.shape || '');

    // Colour, icon or shape changed: rebuild this layer's map layers (the source data is replaced below anyway).
    if (cmap.getSource(pointsId) && drawn.get(l.id) !== style) remove(l.id);

    if (cmap.getSource(pointsId)) {
      cmap.getSource(pointsId).setData(l.points);
      cmap.getSource(circlesId).setData(l.circles);
    } else {
      const c = l.color || colorOf(l.id);
      const pin = l.shape === 'pin';
      let marker = null;
      if (l.iconPath) {
        try { marker = makeMarker(c, l.iconPath, l.shape || 'circle'); } catch (_) { marker = null; } // a bad glyph falls back to the dot
      }

      cmap.addSource(circlesId, { type: 'geojson', data: l.circles });
      cmap.addSource(pointsId, { type: 'geojson', data: l.points });
      // Radius area: light fill + crisp dashed outline, so overlapping circles stay readable.
      cmap.addLayer({ id: lid(l.id, 'fill'), type: 'fill', source: circlesId,
        paint: { 'fill-color': c, 'fill-opacity': 0.14 } });
      cmap.addLayer({ id: lid(l.id, 'line'), type: 'line', source: circlesId,
        layout: { 'line-join': 'round' },
        paint: { 'line-color': c, 'line-width': 1.8, 'line-opacity': 0.9, 'line-dasharray': [3, 2] } });

      if (marker) {
        const name = imgName(l.id);
        if (cmap.hasImage(name)) cmap.removeImage(name);
        cmap.addImage(name, marker, { pixelRatio: 2 });
        // Invisible hit target (hover/click query the 'dot' layer); the icon is what the user sees. A pin's head sits above its tip.
        cmap.addLayer({ id: lid(l.id, 'dot'), type: 'circle', source: pointsId,
          paint: { 'circle-radius': pin ? 15 : 13, 'circle-color': c, 'circle-opacity': 0.01, 'circle-translate': pin ? [0, -18] : [0, 0] } });
        cmap.addLayer({ id: lid(l.id, 'icon'), type: 'symbol', source: pointsId,
          layout: { 'icon-image': name, 'icon-anchor': pin ? 'bottom' : 'center', 'icon-allow-overlap': true, 'icon-ignore-placement': true,
                    'icon-size': ['interpolate', ['linear'], ['zoom'], 3, 0.6, 10, 0.85, 16, 1.1] } });
      } else {
        // Point marker: soft halo + solid dot with a white ring, growing a little with the zoom.
        cmap.addLayer({ id: lid(l.id, 'halo'), type: 'circle', source: pointsId,
          paint: { 'circle-radius': ['interpolate', ['linear'], ['zoom'], 3, 6, 10, 11, 16, 15],
                   'circle-color': c, 'circle-opacity': 0.22, 'circle-blur': 0.4 } });
        cmap.addLayer({ id: lid(l.id, 'dot'), type: 'circle', source: pointsId,
          paint: { 'circle-radius': ['interpolate', ['linear'], ['zoom'], 3, 3.5, 10, 6, 16, 8],
                   'circle-color': c, 'circle-stroke-color': '#FFFFFF', 'circle-stroke-width': 2 } });
      }

      cmap.addLayer({ id: lid(l.id, 'label'), type: 'symbol', source: pointsId, minzoom: 8,
        layout: { 'text-field': ['get', 'name'], 'text-font': FONT, 'text-size': 12, 'text-anchor': 'top',
                  'text-offset': [0, pin ? 0.5 : (marker ? 1.9 : 1.2)], 'text-optional': true },
        paint: { 'text-color': dark ? '#E8ECF8' : '#1B2033', 'text-halo-color': dark ? '#0E1220' : '#FFFFFF', 'text-halo-width': 1.8 } });
      drawn.set(l.id, style);
    }
    setVisible(l.id, l.visible);
    revalidateSelection(l);
    raiseSelection();
  }

  function remove(id) {
    if (!cmap || !ready) return;
    for (const p of PARTS) if (cmap.getLayer(lid(id, p))) cmap.removeLayer(lid(id, p));
    for (const p of ['points', 'circles']) if (cmap.getSource(sid(id, p))) cmap.removeSource(sid(id, p));
    if (cmap.hasImage(imgName(id))) cmap.removeImage(imgName(id)); // after its layer is gone
    drawn.delete(id);
  }

  function fit(id) {
    const l = layers.get(id);
    if (!l || !l.bounds) return;
    if (!cmap || !ready) { pendingFit = id; return; }
    const [w, s, e, n] = l.bounds;
    if (w === e && s === n) cmap.easeTo({ center: [w, s], zoom: Math.max(cmap.getZoom(), 14), duration: 600 });
    else cmap.fitBounds([[w, s], [e, n]], { padding: 60, maxZoom: 17, duration: 600 });
  }

  // ---- selection highlight ----------------------------------------------------------------------------------------

  // A pin's head is drawn above the point itself, so the highlight ring is lifted to the head for pins.
  function drawSelection(coordinates, shape) {
    const data = { type: 'FeatureCollection',
      features: [{ type: 'Feature', geometry: { type: 'Point', coordinates }, properties: {} }] };
    const shift = shape === 'pin' ? [0, -18] : [0, 0];
    if (cmap.getSource(SEL_SRC)) {
      cmap.getSource(SEL_SRC).setData(data);
    } else {
      cmap.addSource(SEL_SRC, { type: 'geojson', data });
      cmap.addLayer({ id: SEL_HALO, type: 'circle', source: SEL_SRC,
        paint: { 'circle-radius': 17, 'circle-color': accent(), 'circle-opacity': 0.28 } });
      cmap.addLayer({ id: SEL_RING, type: 'circle', source: SEL_SRC,
        paint: { 'circle-radius': 10, 'circle-color': accent(), 'circle-opacity': 0,
                 'circle-stroke-color': accent(), 'circle-stroke-width': 3 } });
    }
    cmap.setPaintProperty(SEL_HALO, 'circle-translate', shift);
    cmap.setPaintProperty(SEL_RING, 'circle-translate', shift);
    raiseSelection();
  }

  function raiseSelection() {
    if (!cmap) return;
    if (cmap.getLayer(SEL_HALO)) cmap.moveLayer(SEL_HALO);
    if (cmap.getLayer(SEL_RING)) cmap.moveLayer(SEL_RING);
  }

  function findPoint(layerId, pointId) {
    const l = layers.get(layerId);
    if (!l || !l.points) return null;
    return l.points.features.find((f) => f.properties.id === pointId) || null;
  }

  function select(layerId, pointId) {
    const l = layers.get(layerId), f = findPoint(layerId, pointId);
    if (!l || !f) return;
    selected = { layerId, pointId };
    const [lng, lat] = f.geometry.coordinates;
    drawSelection([lng, lat], l.shape);
    if (l.kind === 'manual') hideCard();
    else showCard(f.properties.name, l.name, lat, lng, f.properties.details);
    post({ type: 'pointSelect', selected: true, name: f.properties.name, layer: l.name, lat, lng });
    window.dispatchEvent(new CustomEvent('coordselect', {
      detail: { layerId, pointId, name: f.properties.name, layerName: l.name, kind: l.kind || 'excel', lat, lng }
    }));
  }

  function clearSelection(notify) {
    hideCard();
    const had = selected !== null;
    selected = null;
    if (cmap && ready && cmap.getSource(SEL_SRC)) cmap.getSource(SEL_SRC).setData(empty());
    if (notify && had) post({ type: 'pointSelect', selected: false });
    if (had) window.dispatchEvent(new CustomEvent('coordselectclear'));
  }

  // After a layer was redrawn: keep the highlight only while its point still exists (and follow it if it moved).
  function revalidateSelection(l) {
    if (!selected || selected.layerId !== l.id) return;
    const f = findPoint(l.id, selected.pointId);
    if (!f || !l.visible) clearSelection(true);
    else {
      drawSelection(f.geometry.coordinates, l.shape);
      if (l.kind !== 'manual') {
        const [lng, lat] = f.geometry.coordinates;
        showCard(f.properties.name, l.name, lat, lng, f.properties.details);
      }
    }
  }

  // ---- hover / click ----------------------------------------------------------------------------------------------

  function hit(point) {
    const ids = [];
    for (const l of layers.values())
      if (l.visible && cmap.getLayer(lid(l.id, 'dot'))) ids.push(lid(l.id, 'dot'));
    if (!ids.length) return null;
    const found = cmap.queryRenderedFeatures(
      [[point.x - HIT_RADIUS, point.y - HIT_RADIUS], [point.x + HIT_RADIUS, point.y + HIT_RADIUS]], { layers: ids });
    if (!found.length) return null;
    const layerId = found[0].layer.id.split('|')[1];
    return { layerId, pointId: found[0].properties.id, name: found[0].properties.name,
             details: found[0].properties.details,
             layerName: (layers.get(layerId) || {}).name || '' };
  }

  function clearHover() {
    const had = hovered !== null;
    hovered = null;
    hideTip();
    if (cmap) cmap.getCanvas().style.cursor = '';
    if (had) post({ type: 'pointHover', name: null, layer: null });
  }

  function onMapOut(e) {
    const next = e && e.originalEvent && e.originalEvent.relatedTarget;
    if (tip && next && tip.contains(next)) return;
    clearHover();
  }

  function onMove(e) {
    if (!cmap || !ready) return;
    const h = hit(e.point);
    if (!h) { if (hovered) clearHover(); return; }
    cmap.getCanvas().style.cursor = 'pointer';
    showTip(e.point, h.name, h.layerName, h.details);
    const key = h.layerId + '|' + h.pointId;
    if (hovered !== key) {
      hovered = key;
      post({ type: 'pointHover', name: h.name, layer: h.layerName });
    }
  }

  // mousemove can fire faster than frames: handle at most one per animation frame.
  function onMoveThrottled(e) {
    moveEvent = e;
    if (moveQueued) return;
    moveQueued = true;
    requestAnimationFrame(() => { moveQueued = false; const ev = moveEvent; moveEvent = null; if (ev) onMove(ev); });
  }

  function onClick(e) {
    if (!cmap || !ready) return;
    const h = hit(e.point);
    if (h) select(h.layerId, h.pointId);
    else clearSelection(true);
  }

  // ---- map.html entry points ----------------------------------------------------------------------------------------

  // Called by map.html every time it creates a map (the old one is already removed).
  window.coordAttach = function (map, isDark) {
    const hadState = selected !== null || hovered !== null;
    cmap = map; ready = false; dark = isDark;
    selected = null; hovered = null; hideTip(); hideCard();
    drawn.clear(); // a new map has none of our layers yet
    if (hadState) { post({ type: 'pointHover', name: null, layer: null }); post({ type: 'pointSelect', selected: false }); }

    map.on('load', () => {
      ready = true;
      for (const l of layers.values()) draw(l);
      if (pendingFit) { const id = pendingFit; pendingFit = null; fit(id); }
    });
    map.on('mousemove', onMoveThrottled);
    map.on('mouseout', onMapOut);
    map.on('dragstart', () => { hideTip(); });
    map.on('zoomstart', () => { hideTip(); });
    map.on('click', onClick);
  };

  window.coordHandle = function (m) {
    switch (m.type) {
      case 'coordUpsert': layers.set(m.id, m); draw(m); break;
      case 'coordVisible': {
        const l = layers.get(m.id);
        if (l) {
          l.visible = m.visible;
          if (cmap && ready) setVisible(m.id, m.visible);
          if (!m.visible) {
            if (selected && selected.layerId === m.id) clearSelection(true);
            if (hovered && hovered.startsWith(m.id + '|')) clearHover();
          }
        }
        break;
      }
      case 'coordRemove':
        if (selected && selected.layerId === m.id) clearSelection(true);
        if (hovered && hovered.startsWith(m.id + '|')) clearHover();
        remove(m.id); layers.delete(m.id);
        break;
      case 'coordFit': fit(m.id); break;
      case 'coordRename': {
        const l = layers.get(m.id);
        if (l) {
          l.name = m.name;
          if (selected && selected.layerId === m.id) select(selected.layerId, selected.pointId); // refresh the info card
        }
        break;
      }
      case 'coordSelectClear': clearSelection(false); break;
    }
  };
})();
