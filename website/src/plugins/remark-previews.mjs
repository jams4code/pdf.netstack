// Remark plugin: a Markdown image pointing at /previews/<name>.webp becomes an <img> with its
// width and height, so nothing shifts while it loads, lazy loading, and the base path. The files
// are written by scripts/previews.py; a missing one fails the build.

import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { visit } from 'unist-util-visit';
import { BASE } from '../site.config.mjs';

const PUBLIC = process.env.WEBSITE_ROOT
  ? join(process.env.WEBSITE_ROOT, 'public')
  : fileURLToPath(new URL('../../public/', import.meta.url));

/** Width and height of a WebP file (lossy VP8, lossless VP8L or extended VP8X). */
export function webpSize(bytes) {
  const tag = (offset, length) => bytes.toString('latin1', offset, offset + length);
  if (tag(0, 4) !== 'RIFF' || tag(8, 4) !== 'WEBP') throw new Error('not a WebP file');
  const chunk = tag(12, 4);
  if (chunk === 'VP8 ') {
    return { width: bytes.readUInt16LE(26) & 0x3fff, height: bytes.readUInt16LE(28) & 0x3fff };
  }
  if (chunk === 'VP8L') {
    const [b0, b1, b2, b3] = bytes.subarray(21, 25);
    return {
      width: 1 + (((b1 & 0x3f) << 8) | b0),
      height: 1 + (((b3 & 0x0f) << 10) | (b2 << 2) | ((b1 & 0xc0) >> 6)),
    };
  }
  if (chunk === 'VP8X') {
    return { width: 1 + bytes.readUIntLE(24, 3), height: 1 + bytes.readUIntLE(27, 3) };
  }
  throw new Error(`unknown WebP chunk ${chunk}`);
}

const escape = (s) => s.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');

export default function remarkPreviews() {
  const base = BASE.replace(/\/$/, '');
  return (tree, file) => {
    visit(tree, 'image', (node, index, parent) => {
      if (!node.url.startsWith('/previews/') || !parent || index === undefined) return;
      const path = node.url.slice(1);
      let size;
      try {
        size = webpSize(readFileSync(join(PUBLIC, path)));
      } catch (error) {
        throw new Error(`${file.path}: preview ${node.url}: ${error.message}. Run scripts/previews.py.`);
      }
      if (!node.alt) throw new Error(`${file.path}: preview ${node.url} has no alt text`);
      parent.children[index] = {
        type: 'html',
        value:
          `<img src="${base}${node.url}" alt="${escape(node.alt)}" width="${size.width}" ` +
          `height="${size.height}" loading="lazy" decoding="async" class="preview">`,
      };
    });
  };
}
