// Report 3's house style, measured from PMSV10_IndDetailedEval.rpt on 2026-09-18, as plan helpers.
const W = 11186;
const INK = '#1F2937', MUTED = '#6B7280', BAR = '#263445', RULE = '#D5D9DE', PANEL = '#F7F9FB', WHITE = '#FFFFFF';

const ops = [];
const font = (t, name, size, bold, color) => {
  ops.push({ action: 'setFont', target: t, fontName: name });
  ops.push({ action: 'setFontSize', target: t, fontSizePt: size });
  ops.push({ action: 'setBold', target: t, bold: !!bold });
  if (color) ops.push({ action: 'setTextColor', target: t, color });
};
// Shrink, move, then size: the validator checks every step against the page edge, so resizing
// in place first (or moving at the old width) can be rejected even when the end state fits.
const geom = (t, l, top, w, h) => {
  ops.push({ action: 'resize', target: t, widthTwips: Math.min(w, 100) || w, heightTwips: h });
  ops.push({ action: 'move', target: t, leftTwips: l, topTwips: top });
  if (w > 100) ops.push({ action: 'resize', target: t, widthTwips: w, heightTwips: h });
};
const box = (t, fill, line) => {
  if (fill) ops.push({ action: 'setFillColor', target: t, color: fill });
  ops.push({ action: 'setLineColor', target: t, color: line });
  ops.push({ action: 'setLineThickness', target: t, lineThicknessTwips: 15 });
};
const line = (t, color) => {
  ops.push({ action: 'setLineColor', target: t, color: color || RULE });
  ops.push({ action: 'setLineThickness', target: t, lineThicknessTwips: 15 });
};

// Page header block: logo 150,100 1600x700; system name, title, confidentiality line at x 1900.
function header({ logo, sys, title, conf, rule }) {
  if (logo) { geom(logo, 150, 100, 1600, 700); ops.push({ action: 'setBorder', target: logo, left: 'none', right: 'none', top: 'none', bottom: 'none' }); }
  geom(sys, 1900, 60, 6000, 250);   font(sys, 'Arial', 9, false, MUTED);
  geom(title, 1900, 330, 7000, 380); font(title, 'Arial', 15, true, INK);
  geom(conf, 1900, 780, 6000, 230); font(conf, 'Arial', 8, false, MUTED);
  if (rule) { geom(rule, 0, 1130, W, 0); line(rule); }
}
// Section banner: 340 tall, #263445, title Arial 9 bold white inset 150 / 95.
function banner(barName, titleName, top) {
  geom(barName, 0, top, W, 340); box(barName, BAR, BAR);
  geom(titleName, 150, top + 95, 8000, 220); font(titleName, 'Arial', 9, true, WHITE);
  return top + 460; // content starts 120 below the banner
}
// Label/value panel: rows 660 apart from panelTop+180; labels Arial 8 grey, values Arial 9 ink.
// rows: [[leftLabel,leftValue,rightLabel,rightValue], ...]; dividers: names for the rules between rows.
function panel(boxName, top, rows, dividers) {
  const h = 180 + rows.length * 660;
  geom(boxName, 0, top, W, h); box(boxName, PANEL, RULE);
  rows.forEach((r, i) => {
    const y = top + 180 + i * 660;
    const [ll, lv, rl, rv] = r;
    if (ll) { geom(ll, 150, y, 2100, 230);  font(ll, 'Arial', 8, false, MUTED); }
    if (lv) { geom(lv, 2350, y, 2450, 260); font(lv, 'Arial', 9, false, INK); }
    if (rl) { geom(rl, 5800, y, 2100, 230); font(rl, 'Arial', 8, false, MUTED); }
    if (rv) { geom(rv, 8000, y, 3036, 260); font(rv, 'Arial', 9, false, INK); }
    if (i < rows.length - 1 && dividers[i]) { geom(dividers[i], 150, y + 480, 10886, 0); line(dividers[i]); }
  });
  return top + h + 200; // next banner
}

module.exports = { ops, font, geom, box, line, header, banner, panel, W, INK, MUTED, BAR, RULE, PANEL, WHITE };
