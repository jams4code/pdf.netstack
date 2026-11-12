// @ts-check
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { defineConfig, passthroughImageService } from 'astro/config';
import { unified } from '@astrojs/markdown-remark';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';
import remarkBase from './src/plugins/remark-base.mjs';
import remarkPreviews from './src/plugins/remark-previews.mjs';
import remarkSamples from './src/plugins/remark-samples.mjs';
import { BASE, CORP, NUGET, REPO, SITE } from './src/site.config.mjs';

// Build time code that reads samples/ and public/ finds them from here, also once bundled.
process.env.WEBSITE_ROOT = fileURLToPath(new URL('.', import.meta.url));

// The API reference groups are written by scripts/api.mjs, one per namespace.
const apiSidebarFile = new URL('./src/generated/api-sidebar.json', import.meta.url);
const apiGroups = existsSync(apiSidebarFile) ? JSON.parse(readFileSync(apiSidebarFile, 'utf8')) : [];

// Comparison pages stay drafts until the owner signs them off. Drafts are left out of production
// builds, so the sidebar group only shows when at least one page is published, or in `astro dev`.
const compareDir = new URL('./src/content/docs/compare/', import.meta.url);
const showCompare =
  process.argv.includes('dev') ||
  (existsSync(compareDir) &&
    readdirSync(compareDir).some(
      (f) => /\.mdx?$/.test(f) && !/^draft:\s*true\s*$/m.test(readFileSync(new URL(f, compareDir), 'utf8')),
    ));

export default defineConfig({
  site: SITE,
  base: BASE || undefined,
  trailingSlash: 'always',
  build: { format: 'directory' },
  // Images are written at their final size by scripts/previews.py; nothing is processed at build.
  image: { service: passthroughImageService() },
  markdown: { processor: unified({ remarkPlugins: [remarkSamples, remarkPreviews, remarkBase] }) },
  integrations: [
    starlight({
      title: 'PdfNetStack',
      description:
        'PdfNetStack is an MIT licensed PDF library for .NET in pure C#: compress, edit, create, sign and extract PDFs, and convert HTML to PDF.',
      // The site title sits next to the logo, so the logo itself is decorative.
      logo: { src: './src/assets/logo.svg', alt: '' },
      favicon: '/favicon.svg',
      titleDelimiter: '|',
      lastUpdated: true,
      editLink: { baseUrl: `${REPO}/edit/main/website/` },
      social: [
        { icon: 'github', label: 'GitHub', href: REPO },
        { icon: 'external', label: 'NuGet', href: NUGET },
        { icon: 'external', label: 'JADEV', href: `${CORP}/en` },
      ],
      customCss: ['./src/styles/custom.css'],
      routeMiddleware: './src/route-data.ts',
      components: {
        Footer: './src/components/Footer.astro',
        MarkdownContent: './src/components/MarkdownContent.astro',
        PageTitle: './src/components/PageTitle.astro',
      },
      sidebar: [
        { label: 'Getting started', slug: 'getting-started' },
        {
          label: 'Guides',
          items: [
            'guides/compress-pdf',
            'guides/edit-pages',
            'guides/large-files',
            'guides/drawing',
            'guides/fonts-and-text',
            'guides/images',
            'guides/forms',
            'guides/annotations',
            'guides/signatures',
            'guides/pdf-a',
            'guides/encryption',
            'guides/extract-text',
            'guides/extract-images',
            'guides/ocr',
            {
              label: 'HTML to PDF',
              items: [
                { label: 'Overview', slug: 'guides/html-to-pdf' },
                'guides/html-to-pdf/paged-media',
                'guides/html-to-pdf/fonts-and-assets',
                'guides/html-to-pdf/css-support',
              ],
            },
            'guides/low-level-objects',
            'guides/aws-lambda',
            'guides/azure-functions',
          ],
        },
        { label: 'Recipes', collapsed: true, items: [{ autogenerate: { directory: 'recipes' } }] },
        { label: 'Command line', slug: 'cli' },
        {
          label: 'Concepts',
          items: [
            'concepts/fidelity',
            'concepts/safety-model',
            'concepts/architecture',
            'concepts/coordinates-and-units',
            'concepts/memory-and-performance',
          ],
        },
        {
          label: 'API reference',
          collapsed: true,
          items: [{ label: 'Overview', slug: 'api' }, ...apiGroups],
        },
        ...(showCompare
          ? [{ label: 'Compare', collapsed: true, items: [{ autogenerate: { directory: 'compare' } }] }]
          : []),
        'benchmarks',
        'faq',
        'ai-assistants',
        'changelog',
        'roadmap',
        'about',
      ],
      plugins: [
        starlightLinksValidator({
          errorOnRelativeLinks: true,
          // Files served next to the pages, not content pages. With a base path, the landing page's
          // hero button is prefixed by src/route-data.ts after validation, so its raw link is skipped.
          exclude: [
            '/llms.txt',
            '/llms-full.txt',
            '/llms-small.txt',
            '/**/*.md',
            '/*.md',
            '/og/**',
            ...(BASE ? ['/getting-started/'] : []),
          ],
        }),
      ],
    }),
  ],
});
