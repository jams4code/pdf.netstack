// One 1200x630 Open Graph image per docs page, at /og/<path>.png, rendered at build time.

import { OGImageRoute } from 'astro-og-canvas';
import { getPages, pageId } from '../../lib/markdown';

const pages = Object.fromEntries(
  (await getPages()).map((entry) => [pageId(entry) || 'index', entry.data]),
);

export const { getStaticPaths, GET } = await OGImageRoute({
  param: 'slug',
  pages,
  getImageOptions: (_path, page: (typeof pages)[string]) => ({
    title: page.hero?.title ?? page.title,
    description: `${page.description}\n\nPdfNetStack, by JADEV`,
    bgGradient: [[15, 23, 42], [30, 58, 138]],
    border: { color: [37, 99, 235], width: 16, side: 'inline-start' },
    padding: 72,
    font: {
      title: { families: ['Inter'], weight: 'Bold', size: 64, color: [255, 255, 255] },
      description: { families: ['Inter'], size: 30, lineHeight: 1.4, color: [191, 219, 254] },
    },
    fonts: [
      './node_modules/@fontsource/inter/files/inter-latin-400-normal.woff',
      './node_modules/@fontsource/inter/files/inter-latin-700-normal.woff',
    ],
  }),
});
