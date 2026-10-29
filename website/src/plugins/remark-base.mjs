// Remark plugin: prefixes root relative links with the configured base path, so content written
// with "/guides/..." links also works when the site is served from a subpath. Covers Markdown
// links and the `href` of MDX components such as <LinkCard>.

import { visit } from 'unist-util-visit';
import { BASE } from '../site.config.mjs';

export default function remarkBase() {
  const base = BASE.replace(/\/$/, '');
  const prefix = (url) =>
    typeof url === 'string' && url.startsWith('/') && !url.startsWith('//') && !url.startsWith(base + '/')
      ? base + url
      : url;
  return (tree) => {
    if (!base) return;
    visit(tree, ['link', 'definition'], (node) => {
      node.url = prefix(node.url);
    });
    visit(tree, ['mdxJsxFlowElement', 'mdxJsxTextElement'], (node) => {
      for (const attribute of node.attributes ?? []) {
        if (attribute.type === 'mdxJsxAttribute' && attribute.name === 'href') {
          attribute.value = prefix(attribute.value);
        }
      }
    });
  };
}
