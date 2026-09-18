const meta = require('./meta.js'); const fs = require('fs'); const out = process.argv[2];
fs.writeFileSync(out + '/meta-r1.json', JSON.stringify(meta('PageHeaderSection1', {})));
fs.writeFileSync(out + '/meta-r2.json', JSON.stringify(meta('empdisplaynumberHeaderSection1', { add: true,
  removeFirst: ['MetaGenLbl', 'MetaGenDate', 'MetaGenTime'],
  narrow: [['FontAnchor', 5600, 250], ['HdrTitle', 5600, 380], ['HdrConf', 5600, 230]] })));
fs.writeFileSync(out + '/meta-r3.json', JSON.stringify(meta('empnumberHeaderSection1', { add: true,
  narrow: [['SysTitle', 5600, 250], ['RptTitle', 5600, 380], ['Conf', 5600, 230]] })));
console.log('plans written');
