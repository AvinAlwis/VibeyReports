const st = require('./style.js'); const { ops, font, geom, box, line, header, banner, panel, W, INK, MUTED } = st;
const GH = 'empdisplaynumberHeaderSection1', GF = 'empdisplaynumberFooterSection1';

header({ logo: 'Subreport1', sys: 'FontAnchor', title: 'HdrTitle', conf: 'HdrConf', rule: 'HdrRule' });
for (const t of ['MetaGenLbl', 'MetaGenDate', 'MetaGenTime']) font(t, 'Arial', 8, false, MUTED);

let y = banner('B1Banner', 'B1Title', 1250);
ops.push({ action: 'removeObject', target: 'B1Rdiv0' }, { action: 'removeObject', target: 'B1Rdiv1' },
         { action: 'removeObject', target: 'B1Rdiv2' }, { action: 'removeObject', target: 'B1Rdiv3' },
         { action: 'removeObject', target: 'B1Ldiv3' });
y = panel('B1Box', y, [
  ['B1L0k', 'B1L0v', 'B1R0k', 'B1R0v'],
  ['B1L1k', 'B1L1v', 'B1R1k', 'B1R1v'],
  ['B1L2k', 'B1L2v', 'B1R2k', 'B1R2v'],
  ['B1L3k', 'B1L3v', 'B1R3k', 'B1R3v']], ['B1Ldiv0', 'B1Ldiv1', 'B1Ldiv2']);

// Overview tiles, Report 3's tile panel: 1200 tall, dividers +120/960, labels +200, values +560 (Arial 14 bold).
y = banner('B2Banner', 'B2Title', y);
geom('B2Box', 0, y, W, 1200); box('B2Box', st.PANEL, '#000000');
geom('TileDiv1', 3728, y + 120, 0, 960); line('TileDiv1');
geom('TileDiv2', 7456, y + 120, 0, 960); line('TileDiv2');
[['TileLbl0', 'B2T0num', 80, 3568], ['TileLbl1', 'B2T1num', 3808, 3568], ['TileLbl2', 'B2T3num', 7536, 3570]].forEach(([l, v, x, w]) => {
  geom(l, x, y + 200, w, 220); font(l, 'Arial', 8, false, MUTED);
  geom(v, x, y + 560, w, 420); font(v, 'Arial', 14, true);          // keeps its green / red
});
y += 1200 + 200;

// Goal table header row.
y = banner('B3Banner', 'B3Title', y);
geom('GTHFill', 0, y, W, 480);
[['GTHV1', 600], ['GTHV2', 3700], ['GTHV3', 5000], ['GTHV4', 9200]].forEach(([n, x]) => geom(n, x, y, 0, 480));
[['GTH0', 70, 460], ['GTH1', 670, 2960], ['GTH2', 3770, 1160], ['GTH3', 5070, 4060], ['GTH4', 9270, 1846]].forEach(([n, x, w]) => {
  geom(n, x, y + 130, w, 260); font(n, 'Segoe UI', 8, true, INK);
});
for (const n of ['GTR0', 'GTR1', 'GTR2', 'GTR3', 'GTR4']) font(n, 'Segoe UI', 8, false, INK);
ops.push({ action: 'resizeSection', section: GH, heightTwips: y + 480 });

// Alignment Legend (group footer): banner 200 below the table, panel under it.
ops.unshift({ action: 'resizeSection', section: GF, heightTwips: 2200 });
let f = banner('B4Banner', 'B4Title', 200);
geom('B4Box', 0, f, W, 1100); box('B4Box', st.PANEL, st.RULE);
geom('LegGlyphAligned', 200, f + 260, 300, 240);    geom('LegTextAligned', 560, f + 260, 9600, 240);
geom('LegGlyphNotAligned', 200, f + 590, 300, 240); geom('LegTextNotAligned', 560, f + 590, 9600, 240);
font('LegTextAligned', 'Arial', 8, true); font('LegTextNotAligned', 'Arial', 8, true);
ops.push({ action: 'resizeSection', section: GF, heightTwips: f + 1100 + 100 });

// Footer: Report 3 carries the confidentiality line on the left.
ops.push({ action: 'addText', section: 'PageFooterSection1', newName: 'FtConf', leftTwips: 0, topTwips: 140, widthTwips: 6000, heightTwips: 220,
           text: 'CONFIDENTIAL - For Internal HR Use Only' });
font('FtConf', 'Arial', 8, false, MUTED);

require('fs').writeFileSync(process.argv[2], JSON.stringify(ops, null, 1));
console.log(ops.length, 'ops; group header', y + 480, 'legend footer', f + 1200);
