import { defineCollection } from 'astro:content';
import { z } from 'astro/zod';
import { docsLoader } from '@astrojs/starlight/loaders';
import { docsSchema } from '@astrojs/starlight/schema';

// Titles and descriptions are required and sized for search results: the page title plus
// " | PdfNetStack" stays within 60 characters, the description within 155.
export const collections = {
  docs: defineCollection({
    loader: docsLoader(),
    schema: docsSchema({
      extend: z.object({
        title: z.string().min(3).max(46),
        description: z.string().min(50).max(155),
        /** First release that has the feature: shown as a badge, with a note while unreleased. */
        since: z.enum(['1.0', '1.1', 'unreleased']).optional(),
        /** Questions and answers rendered by the page and emitted as FAQPage JSON-LD. */
        faq: z.array(z.object({ q: z.string(), a: z.string() })).optional(),
      }),
    }),
  }),
};
