// The top-right "Generated / Format" block, laid out so no viewer can make it overlap:
// labels right-aligned ending at 9400, values LEFT-aligned from 9500, every value box wider than
// its text in any renderer. (Right-aligning the values made their left edge depend on how wide
// the viewer draws the text, and Report Navigator's viewer draws it wider than the PDF export,
// so "Format:" collided with its value.)
const INK = '#1F2937';
module.exports = function meta(section, { add, removeFirst = [], narrow = [] }) {
  const ops = [];
  for (const n of removeFirst) ops.push({ action: 'removeObject', target: n });
  for (const [n, w, h] of narrow) ops.push({ action: 'resize', target: n, widthTwips: w, heightTwips: h });
  const style = (t, bold) => ops.push(
    { action: 'setFont', target: t, fontName: 'Arial' }, { action: 'setFontSize', target: t, fontSizePt: 8 },
    { action: 'setBold', target: t, bold }, { action: 'setTextColor', target: t, color: INK });
  const place = (t, x, y, w, align) => {
    ops.push({ action: 'resize', target: t, widthTwips: 100, heightTwips: 220 },
             { action: 'move', target: t, leftTwips: x, topTwips: y },
             { action: 'resize', target: t, widthTwips: w, heightTwips: 220 },
             { action: 'setAlignment', target: t, alignment: align });
  };
  if (add) {
    ops.push({ action: 'addText', section, newName: 'GenLbl', leftTwips: 7900, topTwips: 60, widthTwips: 1500, heightTwips: 220, text: 'Generated:' });
    ops.push({ action: 'addSpecialField', section, newName: 'GenDate', specialType: 'printDate', leftTwips: 9500, topTwips: 60, widthTwips: 950, heightTwips: 220 });
    ops.push({ action: 'addText', section, newName: 'GenComma', leftTwips: 10260, topTwips: 60, widthTwips: 80, heightTwips: 220, text: ',' });
    ops.push({ action: 'addSpecialField', section, newName: 'GenTime', specialType: 'printTime', leftTwips: 10360, topTwips: 60, widthTwips: 826, heightTwips: 220 });
    ops.push({ action: 'addText', section, newName: 'FmtLbl', leftTwips: 7900, topTwips: 330, widthTwips: 1500, heightTwips: 220, text: 'Format:' });
    ops.push({ action: 'addText', section, newName: 'FmtVal', leftTwips: 9500, topTwips: 330, widthTwips: 1686, heightTwips: 220, text: 'Crystal Reports (.rpt)' });
  }
  place('GenLbl', 7900, 60, 1500, 'Right');  style('GenLbl', true);
  place('GenDate', 9500, 60, 950, 'Left');   style('GenDate', false);
  place('GenComma', 10260, 60, 80, 'Left');  style('GenComma', false);
  place('GenTime', 10360, 60, 826, 'Left');  style('GenTime', false);
  place('FmtLbl', 7900, 330, 1500, 'Right'); style('FmtLbl', true);
  place('FmtVal', 9500, 330, 1686, 'Left');  style('FmtVal', false);
  return ops;
};
