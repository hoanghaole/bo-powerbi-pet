#!/usr/bin/env node
'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');

const PACKAGE = require('../package.json');
const OWNER = 'hoanghaole';
const REPO = 'bo-powerbi-pet';
const PRODUCT_NAME = 'BoBIPet';
const LEGACY_DIR_NAME = 'BoPowerBIPet';
const APP_DIR_NAME = 'BoBIPet';
const EXE_NAME = `${PRODUCT_NAME}.exe`;
const ZIP_NAME = `${PRODUCT_NAME}-win-x64.zip`;
const SUMS_NAME = 'SHA256SUMS';
const RELEASE_BASE = `https://github.com/${OWNER}/${REPO}/releases/download`;

function main(argv = process.argv.slice(2), env = process.env) {
  if (argv.includes('--version') || argv.includes('-v')) {
    process.stdout.write(`${PACKAGE.version}\n`);
    return;
  }

  if (process.platform !== 'win32') {
    fail(`${PRODUCT_NAME} chỉ hỗ trợ Windows. Dùng installer PowerShell fallback nếu cần tải thủ công từ GitHub Release.`);
  }

  const version = PACKAGE.version;
  const releaseDir = getInstallRoot(env);
  const versionDir = path.join(releaseDir, version);
  const exePath = path.join(versionDir, EXE_NAME);

  ensureDir(versionDir);
  migrateLegacyInstall(env, releaseDir);

  if (!fs.existsSync(exePath)) {
    installVersion({ version, releaseDir, versionDir, env });
  }

  launch(exePath, argv);
}

function installVersion({ version, releaseDir, versionDir, env }) {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'bobipet-'));
  try {
    const zipPath = path.join(tempDir, ZIP_NAME);
    const sumsPath = path.join(tempDir, SUMS_NAME);
    downloadToFile(buildReleaseAssetUrl(version, ZIP_NAME), zipPath);
    downloadToFile(buildReleaseAssetUrl(version, SUMS_NAME), sumsPath);
    verifyZipChecksum(zipPath, sumsPath, ZIP_NAME);
    extractZipPowerShell(zipPath, versionDir);
    const exePath = path.join(versionDir, EXE_NAME);
    if (!fs.existsSync(exePath)) {
      fail(`Thiếu ${EXE_NAME} sau khi giải nén ${ZIP_NAME}.`);
    }
    writeCurrentPointer(releaseDir, version);
    ponytailCleanupOldVersions(releaseDir, version);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
}

function migrateLegacyInstall(env, releaseDir) {
  const currentFile = path.join(releaseDir, 'current.txt');
  if (fs.existsSync(currentFile)) return;

  const legacyBase = path.join(getLocalAppData(env), LEGACY_DIR_NAME);
  const legacyExe = path.join(legacyBase, 'BoPowerBIPet.exe');
  if (!fs.existsSync(legacyExe)) return;

  const targetDir = path.join(releaseDir, PACKAGE.version);
  ensureDir(targetDir);
  fs.copyFileSync(legacyExe, path.join(targetDir, EXE_NAME));
  const tokenSrc = path.join(legacyBase, 'token.txt');
  const tokenDst = path.join(targetDir, 'token.txt');
  if (fs.existsSync(tokenSrc) && !fs.existsSync(tokenDst)) fs.copyFileSync(tokenSrc, tokenDst);
  writeCurrentPointer(releaseDir, PACKAGE.version);
}

function launch(exePath, passthroughArgs) {
  const child = spawn(exePath, passthroughArgs, {
    detached: true,
    stdio: 'ignore',
    windowsHide: false
  });
  child.unref();
}

function buildReleaseAssetUrl(version, assetName) {
  return `${RELEASE_BASE}/v${normalizeVersion(version)}/${assetName}`;
}

function normalizeVersion(version) {
  return String(version).replace(/^v/i, '');
}

function getInstallRoot(env) {
  const prefix = getNpmPrefix(env);
  if (prefix) return path.join(prefix, 'bobipet');
  return path.join(getLocalAppData(env), APP_DIR_NAME, 'npm');
}

function getNpmPrefix(env) {
  const candidates = [env.npm_config_prefix, env.PREFIX].filter(Boolean);
  for (const candidate of candidates) {
    if (candidate && isAbsolutePrefix(candidate)) return candidate;
  }
  return '';
}

function isAbsolutePrefix(candidate) {
  return path.isAbsolute(candidate) || /^[A-Za-z]:\\/.test(candidate) || candidate.startsWith('\\\\');
}

function getLocalAppData(env) {
  return env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
}

function ensureDir(dir) {
  fs.mkdirSync(dir, { recursive: true });
}

function downloadToFile(url, destination) {
  const ps = [
    '$ErrorActionPreference = "Stop"',
    '$ProgressPreference = "SilentlyContinue"',
    `[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12`,
    `$wc = New-Object Net.WebClient`,
    `$wc.Headers['User-Agent'] = '${PRODUCT_NAME}-npm/${PACKAGE.version}'`,
    `$wc.DownloadFile('${escapePs(url)}', '${escapePs(destination)}')`
  ].join('; ');
  const result = spawnSyncChecked('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', ps]);
  if (!fs.existsSync(destination) || fs.statSync(destination).size === 0) {
    fail(`Tải file thất bại: ${url}\n${result.stderr}`.trim());
  }
}

function verifyZipChecksum(zipPath, sumsPath, expectedAssetName) {
  const sums = fs.readFileSync(sumsPath, 'utf8');
  const expected = parseSha256Sums(sums, expectedAssetName);
  const actual = sha256File(zipPath);
  if (expected !== actual) {
    fail(`SHA256 mismatch cho ${expectedAssetName}. expected=${expected} actual=${actual}`);
  }
}

function parseSha256Sums(text, assetName) {
  const lines = text.split(/\r?\n/);
  for (const line of lines) {
    const match = line.match(/^([a-fA-F0-9]{64})\s+\*?(.+)$/);
    if (match && match[2].trim() === assetName) return match[1].toLowerCase();
  }
  fail(`Không tìm thấy checksum cho ${assetName} trong ${SUMS_NAME}.`);
}

function sha256File(filePath) {
  const hash = crypto.createHash('sha256');
  hash.update(fs.readFileSync(filePath));
  return hash.digest('hex');
}

function extractZipPowerShell(zipPath, destination) {
  ensureDir(destination);
  const ps = [
    '$ErrorActionPreference = "Stop"',
    `Expand-Archive -LiteralPath '${escapePs(zipPath)}' -DestinationPath '${escapePs(destination)}' -Force`
  ].join('; ');
  spawnSyncChecked('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', ps]);
}

function writeCurrentPointer(releaseDir, version) {
  fs.writeFileSync(path.join(releaseDir, 'current.txt'), `${version}\n`, 'utf8');
}

function ponytailCleanupOldVersions(releaseDir, keepVersion) {
  // ponytail: keep simple eager cleanup; upgrade to cache retention if users need rollback.
  for (const entry of fs.readdirSync(releaseDir, { withFileTypes: true })) {
    if (!entry.isDirectory() || entry.name === keepVersion) continue;
    fs.rmSync(path.join(releaseDir, entry.name), { recursive: true, force: true });
  }
}

function escapePs(value) {
  return String(value).replace(/'/g, "''");
}

function spawnSyncChecked(command, args) {
  const { spawnSync } = require('node:child_process');
  const result = spawnSync(command, args, { encoding: 'utf8' });
  if (result.status !== 0) {
    fail((result.stderr || result.stdout || `${command} failed`).trim());
  }
  return result;
}

function fail(message) {
  throw new Error(message);
}

if (require.main === module) {
  try {
    main();
  } catch (error) {
    process.stderr.write(`${PRODUCT_NAME}: ${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = {
  APP_DIR_NAME,
  EXE_NAME,
  LEGACY_DIR_NAME,
  PRODUCT_NAME,
  SUMS_NAME,
  ZIP_NAME,
  buildReleaseAssetUrl,
  getInstallRoot,
  getLocalAppData,
  getNpmPrefix,
  isAbsolutePrefix,
  normalizeVersion,
  parseSha256Sums,
  sha256File,
  verifyZipChecksum
};
