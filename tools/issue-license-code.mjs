#!/usr/bin/env node
// ══════════════════════════════════════════════════════════════════════
//  issue-license-code.mjs — 批量签发激活码（离线，纯 HMAC，不联网、不建表）
//
//  用法：
//    $env:CSH_LICENSE_SECRET = '<密钥>'
//    node tools\issue-license-code.mjs 5
//
//  也可把密钥写进 .secrets\license-secret.txt（已在 .gitignore 里挡住）。
//  签出的码给对方后，对方在「设置 → 授权 → 正版授权」里输入即可激活；
//  服务端的 CSH_LICENSE_SECRET 必须是同一把，否则验不过。
//
//  码面结构（16 字符）：
//    payload 5 字节 = 签发起始日（自 2020-01-01 的天数, uint16 BE） + 序列号（uint24 BE）
//    mac     5 字节 = HMAC-SHA256(secret, "code|" + payloadHex)[0..5]
// ══════════════════════════════════════════════════════════════════════

import { createHmac, randomInt } from 'node:crypto';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ALPHABET = '23456789ABCDEFGHJKLMNPQRSTUVWXYZ';
const EPOCH = Date.UTC(2020, 0, 1);
const root = join(dirname(fileURLToPath(import.meta.url)), '..');

function secret() {
  if (process.env.CSH_LICENSE_SECRET) return process.env.CSH_LICENSE_SECRET.trim();
  const file = join(root, '.secrets', 'license-secret.txt');
  if (existsSync(file)) {
    const text = readFileSync(file, 'utf8').trim();
    if (text) return text;
  }
  console.error('缺少 CSH_LICENSE_SECRET（环境变量或 .secrets\\license-secret.txt）');
  process.exit(1);
}

function base32Encode(bytes) {
  let bits = 0;
  let value = 0;
  let out = '';
  for (const byte of bytes) {
    value = (value << 8) | byte;
    bits += 8;
    while (bits >= 5) {
      out += ALPHABET[(value >>> (bits - 5)) & 31];
      bits -= 5;
    }
  }
  if (bits > 0) out += ALPHABET[(value << (5 - bits)) & 31];
  return out;
}

function makeCode(key, serial, date = new Date()) {
  const days = Math.floor((date.getTime() - EPOCH) / 86400000);
  if (days < 0 || days > 0xffff) throw new Error('日期超出可编码范围');

  const payload = Buffer.from([
    (days >>> 8) & 0xff,
    days & 0xff,
    (serial >>> 16) & 0xff,
    (serial >>> 8) & 0xff,
    serial & 0xff,
  ]);

  const mac = createHmac('sha256', key).update(`code|${payload.toString('hex')}`).digest();
  const code = base32Encode(Buffer.concat([payload, mac.subarray(0, 5)]));
  return code.match(/.{1,4}/g).join('-');
}

const count = Math.max(1, Number(process.argv[2] || 1));
const key = secret();

console.log(`# 签发 ${count} 个激活码`);
for (let i = 0; i < count; i++) {
  const serial = randomInt(0, 0xffffff);
  console.log('CSH-' + makeCode(key, serial));
}
