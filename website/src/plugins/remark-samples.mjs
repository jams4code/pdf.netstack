// Remark plugin: a code fence with `sample="name.cs"` (and optionally `region="name"`) gets the
// sample's code from website/samples, so pages always show code that CI compiled and ran. A fence
// with `generated="name.txt"` gets a file the build wrote into src/generated, such as the
// command line help.

import { visit } from 'unist-util-visit';
import { parseSampleMeta, renderFence } from '../lib/samples.mjs';

export default function remarkSamples() {
  return (tree) => {
    visit(tree, 'code', (node) => {
      const parsed = parseSampleMeta(node.meta);
      if (!parsed) return;
      const { code, title } = renderFence(parsed);
      node.value = code.trimEnd();
      node.meta = [title && `title="${title}"`, parsed.rest].filter(Boolean).join(' ') || null;
    });
  };
}
