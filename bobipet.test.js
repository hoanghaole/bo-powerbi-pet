'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const bobipet = require('./bin/bobipet.js');

test('buildReleaseAssetUrl dùng repo/tag đúng', () => {
  assert.equal(
    bobipet.buildReleaseAssetUrl('3.1.4', 'BoBIPet-win-x64.zip'),
    'https://github.com/hoanghaole/bo-powerbi-pet/releases/download/v3.1.4/BoBIPet-win-x64.zip'
  );
  assert.equal(bobipet.buildReleaseAssetUrl('v3.1.4', 'SHA256SUMS'), 'https://github.com/hoanghaole/bo-powerbi-pet/releases/download/v3.1.4/SHA256SUMS');
});

test('parseSha256Sums đọc đúng checksum asset', () => {
  const sums = [
    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa *Other.zip',
    'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb *BoBIPet-win-x64.zip'
  ].join('\n');
  assert.equal(
    bobipet.parseSha256Sums(sums, 'BoBIPet-win-x64.zip'),
    'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
  );
});

test('verifyZipChecksum pass/fail đúng', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bobipet-test-'));
  try {
    const zip = path.join(dir, 'BoBIPet-win-x64.zip');
    fs.writeFileSync(zip, 'zip-data');
    const digest = crypto.createHash('sha256').update('zip-data').digest('hex');
    const sums = path.join(dir, 'SHA256SUMS');
    fs.writeFileSync(sums, `${digest} *BoBIPet-win-x64.zip\n`, 'utf8');
    assert.doesNotThrow(() => bobipet.verifyZipChecksum(zip, sums, 'BoBIPet-win-x64.zip'));

    fs.writeFileSync(sums, `${'0'.repeat(64)} *BoBIPet-win-x64.zip\n`, 'utf8');
    assert.throws(() => bobipet.verifyZipChecksum(zip, sums, 'BoBIPet-win-x64.zip'), /SHA256 mismatch/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('prefix Windows absolute nhận đúng', () => {
  assert.equal(bobipet.isAbsolutePrefix('C:\\Users\\hao\\AppData\\Roaming\\npm'), true);
  assert.equal(bobipet.isAbsolutePrefix('\\\\server\\share\\npm'), true);
  assert.equal(bobipet.isAbsolutePrefix('relative\\npm'), false);
});

test('getInstallRoot ưu tiên npm prefix, fallback LOCALAPPDATA', () => {
  assert.equal(
    bobipet.getInstallRoot({ npm_config_prefix: 'C:\\Users\\hao\\AppData\\Roaming\\npm', LOCALAPPDATA: 'C:\\Users\\hao\\AppData\\Local' }),
    path.join('C:\\Users\\hao\\AppData\\Roaming\\npm', 'bobipet')
  );
  assert.equal(
    bobipet.getInstallRoot({ LOCALAPPDATA: 'C:\\Users\\hao\\AppData\\Local' }),
    path.join('C:\\Users\\hao\\AppData\\Local', 'BoBIPet', 'npm')
  );
});
