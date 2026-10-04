// publish-release-win7.mjs — 发一个「Windows 7 移植版」的 GitHub Release
//
// 目标仓库：ClassSoftwareHub-windows7（Windows 7 版**自己的仓库**，2026-10-04 起独立成库）。
//   · 跟 WinUI 版（ClassSoftwareHub-Desktop）彻底分家：两边的 Release 列表、更新日志各看各的，
//     不存在"把另一个版本的更新推给用户"这种事，所以 **tag 不再需要 win7- 前缀**。
//   · 这里刻意保留 tag 前缀机制（TAG_PREFIX），留空 = 不加前缀；哪天又想合库，填上即可，
//     同时要把 src-win7/Core/ShellConfig.cs 的 UpdateTagPrefix 改成一样的值。
//
// 用法（在工程根目录）：
//   set GITHUB_TOKEN=ghp_xxx            # 只在当前终端，别写进任何文件
//   node tools/publish-release-win7.mjs --version 1.0.0 ^
//        --installer "dist\installer\ClassSoftwareHub-Setup-win7-dv1.0.0.exe" ^
//        --portable  "dist\ClassSoftwareHub-Portable-win7-dv1.0.0.zip" ^
//        --image     "dist\release\dv1.0.png" ^
//        --notes     notes.md
//
// 参数：
//   --version    必填。不带 dv / 前缀，如 1.0.0（= ShellConfig.ShellVersion）
//   --channel    stable | insider；省略时按 version 里有没有 insider 自动判
//   --installer  必填。安装包路径；同名 .md5 自动生成并一起上传
//   --portable   可选。便携版 zip；同名 .md5 自动生成并一起上传
//   --image      可选。Release 配图（png）
//   --notes      可选。更新说明文件（md/txt，原样显示在更新对话框里）
//   --name       可选。Release 标题，默认「ClassSoftwareHub dv<版本> (Windows 7)」
//   --draft      可选。发成草稿
//   --dry-run    可选。只打印要干什么，不碰 GitHub

import { createHash } from 'node:crypto';
import { readFile, writeFile, stat } from 'node:fs/promises';
import { basename, resolve } from 'node:path';

const OWNER = 'c1201y';
const REPO = 'ClassSoftwareHub-windows7';
const TAG_PREFIX = '';             // 独立仓库后留空；填了必须和 ShellConfig.UpdateTagPrefix 一致

// ── 参数 ────────────────────────────────────────────────────────────
const args = process.argv.slice(2);
function opt(name, fallback = '') {
  const i = args.indexOf('--' + name);
  if (i < 0) return fallback;
  const v = args[i + 1];
  return !v || v.startsWith('--') ? 'true' : v;
}

const version = opt('version').replace(/^(win7-)?(dv)?/i, '');
const installer = opt('installer');
const portable = opt('portable');
const imageFile = opt('image');
const notesFile = opt('notes');
const isDraft = args.includes('--draft');
const dryRun = args.includes('--dry-run');
const token = process.env.GITHUB_TOKEN || process.env.GH_TOKEN || '';

if (!version || !installer) {
  console.error('缺参数：--version 和 --installer 是必填。见文件头的用法。');
  process.exit(2);
}

const tag = TAG_PREFIX + 'dv' + version;
const looksPrerelease = version.includes('insider');
let channel = (opt('channel') || (looksPrerelease ? 'insider' : 'stable')).toLowerCase();

if (channel !== 'stable' && channel !== 'insider') {
  console.error(`--channel 只能是 stable / insider（现在给的是 "${channel}"）`);
  process.exit(2);
}
if (looksPrerelease && channel === 'stable') {
  console.error(`版本 "${version}" 看着是预发布，但 --channel stable。要么改版本号，要么 --channel insider。`);
  process.exit(2);
}

const releaseName = opt('name') || `ClassSoftwareHub dv${version} (Windows 7)`;

if (!token && !dryRun) {
  console.error('没有 GITHUB_TOKEN（或 GH_TOKEN）环境变量，发不了 Release。');
  process.exit(2);
}

const api = 'https://api.github.com';
const headers = {
  Accept: 'application/vnd.github+json',
  'X-GitHub-Api-Version': '2022-11-28',
  'User-Agent': 'ClassSoftwareHub-Release-Win7',
  ...(token ? { Authorization: 'Bearer ' + token } : {}),
};

// ── 算哈希 + 写 .md5 ─────────────────────────────────────────────────
async function prepareAsset(file, required) {
  const path = resolve(file);
  let buf;
  try {
    buf = await readFile(path);
  } catch {
    if (required) {
      console.error(`找不到文件：${path}`);
      process.exit(2);
    }
    return null;
  }
  const md5 = createHash('md5').update(buf).digest('hex');
  const md5Path = path + '.md5';
  // "<md5>  <文件名>"：更新器既能按文件名匹配，也能整段正则抓到
  await writeFile(md5Path, `${md5}  ${basename(path)}\n`, 'utf8');
  return { path, md5Path, md5, sizeMB: (buf.length / 1024 / 1024).toFixed(1) };
}

const installerAsset = await prepareAsset(installer, true);
const portableAsset = portable ? await prepareAsset(portable, true) : null;

const body = notesFile ? await readFile(resolve(notesFile), 'utf8') : '';

// 配图（可选）。给了就必须存在 —— 不然正文首行那条 ![]() 会挂一张 404 的图。
const imagePath = imageFile ? resolve(imageFile) : '';
if (imagePath) {
  try {
    await stat(imagePath);
  } catch {
    console.error(`--image 指的图不存在：${imagePath}`);
    process.exit(2);
  }
  if (!/\.png$/i.test(imagePath)) {
    console.error(`--image 建议用 png（现在给的是 "${basename(imagePath)}"）。`);
    process.exit(2);
  }
}

console.log('── 要发的 Release（Windows 7 版）──────────');
console.log(`仓库      : ${OWNER}/${REPO}`);
console.log(`tag       : ${tag}${isDraft ? ' (草稿)' : ''}`);
console.log(`通道      : ${channel}${channel === 'insider' ? ' → 勾 Pre-release' : ' → 正式版'}`);
console.log(`标题      : ${releaseName}`);
console.log(`安装包    : ${basename(installerAsset.path)}  ${installerAsset.sizeMB} MB`);
console.log(`           MD5 ${installerAsset.md5}`);
if (portableAsset) {
  console.log(`便携包    : ${basename(portableAsset.path)}  ${portableAsset.sizeMB} MB`);
  console.log(`           MD5 ${portableAsset.md5}`);
}
console.log(`说明      : ${body ? notesFile + '（' + body.length + ' 字）' : '（空）'}`);
console.log(`配图      : ${imagePath ? basename(imagePath) : '（没给 --image）'}`);

if (dryRun) {
  console.log('\n--dry-run：到此为止，没动 GitHub。');
  process.exit(0);
}

// ── 建 Release ──────────────────────────────────────────────────────
async function gh(url, init = {}) {
  const r = await fetch(url, { ...init, headers: { ...headers, ...(init.headers || {}) } });
  const text = await r.text();
  if (!r.ok) throw new Error(`${init.method || 'GET'} ${url} → ${r.status}\n${text.slice(0, 400)}`);
  return text ? JSON.parse(text) : null;
}

console.log('\n1/2 创建 Release…');
const existing = await gh(`${api}/repos/${OWNER}/${REPO}/releases/tags/${encodeURIComponent(tag)}`).catch(() => null);
if (existing) {
  console.error(`  已经存在 tag ${tag} 的 Release 了：${existing.html_url}\n  要么版本号加一，要么去网页上删了重来。`);
  process.exit(3);
}

const release = await gh(`${api}/repos/${OWNER}/${REPO}/releases`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    tag_name: tag,
    name: releaseName,
    body,
    draft: isDraft,
    prerelease: channel === 'insider',
  }),
});
console.log('   ✓ ' + release.html_url);

console.log('2/2 上传资产…');
// 顺序：先传安装包与便携包（客户端认这个），再传配图（正文首行 ![]() 指向它）
const uploads = [
  installerAsset.path, installerAsset.md5Path,
  ...(portableAsset ? [portableAsset.path, portableAsset.md5Path] : []),
  ...(imagePath ? [imagePath] : []),
];
for (const file of uploads) {
  const data = await readFile(file);
  const name = basename(file);
  const uploadUrl = `https://uploads.github.com/repos/${OWNER}/${REPO}/releases/${release.id}/assets?name=${encodeURIComponent(name)}`;
  const asset = await gh(uploadUrl, {
    method: 'POST',
    headers: { 'Content-Type': 'application/octet-stream', 'Content-Length': String(data.length) },
    body: data,
    duplex: 'half',
  });
  console.log(`   ✓ ${name}（${(asset.size / 1024 / 1024).toFixed(1)} MB）`);
}

console.log(`\n完成 🎉  ${release.html_url}`);
console.log(`客户端：Windows 7 版（独立仓库 ${OWNER}/${REPO}）的用户会收到这个版本；WinUI 版不受影响。`);
