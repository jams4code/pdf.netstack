// Per page head tags: JSON-LD, Open Graph image, the Markdown twin link, and the pre-launch
// noindex. Starlight runs this for every docs page before rendering.

import { defineRouteMiddleware } from '@astrojs/starlight/route-data';
import { BASE, CORP, NUGET, ORG_ID, PERSON_ID, PRELAUNCH, RELEASE, REPO, SITE } from './site.config.mjs';
import { markdownPath, ogImagePath, pageUrl } from './lib/paths.mjs';

const SOFTWARE_ID = `${SITE}/#software`;

/** Sections whose index page exists, used as the middle breadcrumb. */
const SECTION_INDEX: Record<string, string> = {
  recipes: 'Recipes',
  api: 'API reference',
  compare: 'Compare',
};

function breadcrumbs(id: string, title: string) {
  const items = [{ name: 'PdfNetStack', url: pageUrl('') }];
  const parts = id.split('/');
  if (parts.length > 1 && SECTION_INDEX[parts[0]]) {
    items.push({ name: SECTION_INDEX[parts[0]], url: pageUrl(parts[0]) });
  }
  items.push({ name: title, url: pageUrl(id) });
  return {
    '@type': 'BreadcrumbList',
    itemListElement: items.map((item, i) => ({
      '@type': 'ListItem',
      position: i + 1,
      name: item.name,
      item: item.url,
    })),
  };
}

function softwareNodes() {
  return [
    {
      '@type': 'SoftwareSourceCode',
      '@id': SOFTWARE_ID,
      name: 'PdfNetStack',
      description:
        'MIT licensed PDF library for .NET in pure C#: lossless compression, editing, drawing, forms, signatures, PDF/A, text and image extraction, and HTML to PDF.',
      url: pageUrl(''),
      codeRepository: REPO,
      programmingLanguage: 'C#',
      runtimePlatform: '.NET',
      license: 'https://opensource.org/licenses/MIT',
      author: { '@id': PERSON_ID },
      publisher: { '@id': ORG_ID },
      sameAs: [REPO, NUGET],
    },
    {
      '@type': 'SoftwareApplication',
      '@id': `${SITE}/#application`,
      name: 'PdfNetStack',
      url: pageUrl(''),
      applicationCategory: 'DeveloperApplication',
      operatingSystem: 'Windows, Linux, macOS',
      // The version on NuGet, from the release table, so the structured data follows each release.
      softwareVersion: RELEASE.packages.PdfNetStack,
      softwareRequirements: '.NET 8 or later, or .NET Framework 4.6.2 or later through .NET Standard 2.0',
      featureList: [
        'Lossless PDF compression with verified output',
        'Merge, split, reorder and rotate pages',
        'AES-256 encryption',
        'AcroForm filling and creation',
        'PAdES digital signatures',
        'PDF/A, Factur-X and ZUGFeRD',
        'Text and image extraction',
        'HTML and CSS to PDF without a browser',
        'OCR for scanned PDFs',
      ],
      isAccessibleForFree: true,
      offers: { '@type': 'Offer', price: '0', priceCurrency: 'USD' },
      downloadUrl: NUGET,
      installUrl: NUGET,
      license: 'https://opensource.org/licenses/MIT',
      author: { '@id': PERSON_ID },
      publisher: { '@id': ORG_ID },
      sameAs: [REPO, NUGET],
    },
    {
      '@type': 'WebSite',
      '@id': `${SITE}/#website`,
      name: 'PdfNetStack',
      url: pageUrl(''),
      publisher: { '@id': ORG_ID },
    },
  ];
}

/**
 * JADEV and Jamal Abdelkhalek, by the ids the corporate site defines, so search engines merge both
 * sites into one organization and one person. The profiles are the ones the corporate site lists.
 */
function entities() {
  return [
    {
      '@type': 'Organization',
      '@id': ORG_ID,
      name: 'JADEV',
      url: CORP,
      description: 'Software engineering company that builds and modernizes business software.',
      sameAs: [
        'https://www.linkedin.com/company/jadev-corp/',
        'https://www.linkedin.com/company/jadev-group/',
        'https://www.nuget.org/profiles/jadev',
      ],
    },
    {
      '@type': 'Person',
      '@id': PERSON_ID,
      name: 'Jamal Abdelkhalek',
      url: `${CORP}/jamal`,
      jobTitle: 'Founder',
      worksFor: { '@id': ORG_ID },
      sameAs: ['https://github.com/jams4code', 'https://www.linkedin.com/in/jamal-software-engineer/'],
    },
  ];
}

export const onRequest = defineRouteMiddleware((context) => {
  const route = context.locals.starlightRoute;
  const { entry, head } = route;
  const id = entry.id === 'index' ? '' : entry.id;
  const { title, description, faq } = entry.data;
  const url = pageUrl(id);
  const isHome = id === '';
  const isApi = id === 'api' || id.startsWith('api/');

  // The not found page has no Markdown twin, image or structured data, and is never indexed.
  if (id === '404') {
    head.push({ tag: 'meta', attrs: { name: 'robots', content: 'noindex' } });
    return;
  }

  // Hero buttons are frontmatter, which the Markdown plugins never see: add the base path here.
  const base = BASE.replace(/\/$/, '');
  for (const action of entry.data.hero?.actions ?? []) {
    if (base && action.link.startsWith('/') && !action.link.startsWith(base + '/')) {
      action.link = base + action.link;
    }
  }

  // The landing page title already names the product.
  if (isHome) {
    const tag = head.find((t) => t.tag === 'title');
    if (tag) tag.content = title;
  }

  const ogImage = new URL(ogImagePath(id), SITE).href;
  head.push(
    { tag: 'meta', attrs: { property: 'og:image', content: ogImage } },
    { tag: 'meta', attrs: { property: 'og:image:width', content: '1200' } },
    { tag: 'meta', attrs: { property: 'og:image:height', content: '630' } },
    { tag: 'meta', attrs: { name: 'twitter:image', content: ogImage } },
    {
      tag: 'link',
      attrs: { rel: 'alternate', type: 'text/markdown', href: markdownPath(id), title: 'Markdown' },
    },
  );
  if (isHome) {
    const ogType = head.find((t) => t.tag === 'meta' && t.attrs?.property === 'og:type');
    if (ogType?.attrs) ogType.attrs.content = 'website';
  }
  if (PRELAUNCH) {
    head.push({ tag: 'meta', attrs: { name: 'robots', content: 'noindex, nofollow' } });
  }

  const graph: object[] = [];
  if (isHome) {
    graph.push(...softwareNodes(), ...entities());
  } else if (isApi) {
    graph.push(breadcrumbs(id, title));
  } else if (id === 'about') {
    graph.push(
      {
        '@type': 'AboutPage',
        name: title,
        description,
        url,
        about: { '@id': SOFTWARE_ID },
        author: { '@id': PERSON_ID },
        publisher: { '@id': ORG_ID },
      },
      ...entities(),
      breadcrumbs(id, title),
    );
  } else {
    graph.push(
      {
        '@type': 'TechArticle',
        headline: title,
        description,
        url,
        inLanguage: 'en',
        ...(route.lastUpdated ? { dateModified: route.lastUpdated.toISOString() } : {}),
        author: { '@id': PERSON_ID },
        publisher: { '@id': ORG_ID },
        about: { '@id': SOFTWARE_ID },
        image: ogImage,
      },
      breadcrumbs(id, title),
    );
  }
  if (faq?.length) {
    graph.push({
      '@type': 'FAQPage',
      mainEntity: faq.map(({ q, a }) => ({
        '@type': 'Question',
        name: q,
        acceptedAnswer: { '@type': 'Answer', text: a },
      })),
    });
  }
  head.push({
    tag: 'script',
    attrs: { type: 'application/ld+json' },
    content: JSON.stringify({ '@context': 'https://schema.org', '@graph': graph }).replace(/</g, '\\u003c'),
  });
});
