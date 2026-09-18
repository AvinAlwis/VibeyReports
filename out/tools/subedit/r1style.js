const st = require('./style.js'); const { ops, font, geom, box, line, header, banner, panel, W, INK, MUTED, PANEL, RULE } = st;
const PH = 'PageHeaderSection1', GH = 'empdisplaynumberHeaderSection1', GF = 'empdisplaynumberFooterSection1', PF = 'PageFooterSection1';
const rm = (...n) => n.forEach(t => ops.push({ action: 'removeObject', target: t }));
const add = (o) => ops.push(o);
const CONF = 'CONFIDENTIAL - For Internal HR Use Only';

// ---- Page header: same block as Report 3. The confidentiality line is italic, which no
//      operation can unset, so it is re-added.
rm('Text28');
add({ action: 'addText', section: PH, newName: 'Conf', leftTwips: 1900, topTwips: 780, widthTwips: 6000, heightTwips: 230, text: CONF });
add({ action: 'addLine', section: PH, newName: 'HdrRule', leftTwips: 0, topTwips: 1130, widthTwips: W, heightTwips: 0 });
header({ logo: 'Subreport1', sys: 'Text29', title: 'Text27', conf: 'Conf', rule: 'HdrRule' });
add({ action: 'resizeSection', section: PH, heightTwips: 1250 });

// ---- Group header: rebuilt in Report 3's panel pattern.
add({ action: 'resizeSection', section: GH, heightTwips: 7400 });
rm('Box8', 'Box9', 'Box10', 'Line6', 'Line7', 'Line8', 'Line9', 'Box11', 'Box12', 'Line10', 'Line12', 'Line13', 'Line14', 'Box14', 'Box17', 'Box18');
for (const n of ['EmpBox', 'CycBox', 'TileBox']) add({ action: 'addBox', section: GH, newName: n, leftTwips: 0, topTwips: 0, widthTwips: 1000, heightTwips: 100 });
for (const n of ['EDiv0', 'CDiv0']) add({ action: 'addLine', section: GH, newName: n, leftTwips: 150, topTwips: 50, widthTwips: 1000, heightTwips: 0 });
add({ action: 'addLine', section: GH, newName: 'TDiv1', leftTwips: 5593, topTwips: 0, widthTwips: 0, heightTwips: 100 });

let y = banner('Banner_Text9', 'Text9', 0);
y = panel('EmpBox', y, [['Text3', 'employeename1', 'Text4', 'designation1'], ['Text2', 'empdisplaynumber1', 'Text5', 'department1']], ['EDiv0']);
y = banner('Banner_Text12', 'Text12', y);
y = panel('CycBox', y, [['Text6', 'cyclename1', 'Text7', 'evaluationperiod1'], ['Text10', 'appraisername1', 'Text11', 'reviewername1']], ['CDiv0']);

// Tiles: Report 3's tile panel, two tiles instead of four.
y = banner('Banner_Text13', 'Text13', y);
geom('TileBox', 0, y, W, 1200); box('TileBox', PANEL, '#000000');
geom('TDiv1', 5593, y + 120, 0, 960); line('TDiv1');
const L = 80, R = 5673, TW = 5433;
geom('Text30', L, y + 200, TW, 220); font('Text30', 'Arial', 8, false, MUTED); add({ action: 'setAlignment', target: 'Text30', alignment: 'Centre' });
geom('Text35', R, y + 200, TW, 220); font('Text35', 'Arial', 8, false, MUTED); add({ action: 'setAlignment', target: 'Text35', alignment: 'Centre' });
geom('Text32', L, y + 560, TW, 420); font('Text32', 'Arial', 14, true, INK);
geom('Text36', R, y + 560, TW, 420); font('Text36', 'Arial', 14, true, INK);
geom('Text33', L, y + 980, TW, 200); font('Text33', 'Arial', 8, false, MUTED);
geom('Text37lead', R, y + 980, 2690, 200); font('Text37lead', 'Arial', 8, false, MUTED);   // "/" right-aligned to the tile centre
geom('Text37', R + 2720, y + 980, 2700, 200); font('Text37', 'Arial', 8, false, MUTED);     // the total, left-aligned from it
y += 1200 + 200;

// Stage table header row.
y = banner('Banner_Text19', 'Text19', y);
geom('StageHdrBox', 0, y, W, 480);
for (const [n, x] of [['StageHdrV1', 3960], ['StageHdrV2', 7050], ['StageHdrV3', 8950]]) add({ action: 'move', target: n, leftTwips: x, topTwips: y });
const cols = [['sth_stage_name', 'str_stage_name', 70, 3820], ['sth_stage_period', 'str_stage_period', 4030, 2950],
              ['sth_stage_status', 'str_stage_status', 7120, 1760], ['sth_stage_outcome_score', 'str_stage_outcome_score', 9020, 2096]];
for (const [h, d, x, w] of cols) {
  geom(h, x, y + 130, w, 260); font(h, 'Segoe UI', 8, true, INK);
  geom(d, x, 90, w, 220);      font(d, 'Segoe UI', 8, false, INK);
}
geom('StageRowBox', 0, 0, W, 400);
add({ action: 'resizeSection', section: GH, heightTwips: y + 480 });

// ---- Final comments: three Report-3 panels under one banner, 200 below the table.
add({ action: 'resizeSection', section: GF, heightTwips: 5200 });
let f = banner('Banner_Text20', 'Text20', 200);
for (const [bx, title, who, val] of [['Box3', 'Text21', null, 'overallcomment1'], ['Box4', 'Text23', 'appraisername2', 'appraisercomment1'], ['Box5', 'Text24', 'reviewername2', 'reviewercomment1']]) {
  geom(bx, 0, f, W, 1300); box(bx, PANEL, RULE);
  geom(title, 150, f + 150, 6000, 240); font(title, 'Arial', 9, true, INK);
  if (who) { geom(who, 7000, f + 150, 4036, 220); font(who, 'Arial', 8, false, MUTED); add({ action: 'setAlignment', target: who, alignment: 'Right' }); }
  geom(val, 150, f + 450, 10886, 700); font(val, 'Arial', 9, false, INK);
  f += 1300 + 120;
}
add({ action: 'resizeSection', section: GF, heightTwips: f - 120 + 100 });

// ---- Page footer: rule, confidentiality line, Page N of M.
rm('PageNumber1');
add({ action: 'addLine', section: PF, newName: 'FtRule', leftTwips: 0, topTwips: 40, widthTwips: W, heightTwips: 0 }); line('FtRule');
add({ action: 'addText', section: PF, newName: 'FtConf', leftTwips: 0, topTwips: 140, widthTwips: 6000, heightTwips: 220, text: CONF });
font('FtConf', 'Arial', 8, false, MUTED);
add({ action: 'addSpecialField', section: PF, newName: 'PageNo', specialType: 'pageNOfM', leftTwips: 9186, topTwips: 140, widthTwips: 2000, heightTwips: 220 });
font('PageNo', 'Arial', 8, false, MUTED); add({ action: 'setAlignment', target: 'PageNo', alignment: 'Right' });
add({ action: 'resizeSection', section: PF, heightTwips: 420 });

require('fs').writeFileSync(process.argv[2], JSON.stringify(ops, null, 1));
console.log(ops.length, 'ops; group header', y + 480, 'comments footer', f - 20);
