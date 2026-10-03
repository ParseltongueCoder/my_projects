/**
 * Pretty-prints an XML document for display (raw feed messages arrive on one line). Falls back to the
 * input when it does not parse. Uses the browser's DOMParser - no dependency.
 */
export function formatXml(xml: string, indent = '  '): string {
  const doc = new DOMParser().parseFromString(xml, 'application/xml');
  if (doc.getElementsByTagName('parsererror').length > 0) {
    return xml;
  }
  const declaration = /^<\?xml[^>]*\?>/.exec(xml.trim())?.[0];
  const lines: string[] = declaration ? [declaration] : [];

  const walk = (node: Element, depth: number): void => {
    const pad = indent.repeat(depth);
    const attrs = Array.from(node.attributes).map((a) => ` ${a.name}="${escapeAttr(a.value)}"`).join('');
    const children = Array.from(node.children);
    const text = children.length === 0 ? (node.textContent ?? '').trim() : '';
    if (children.length === 0 && !text) {
      lines.push(`${pad}<${node.tagName}${attrs}/>`);
    } else if (children.length === 0) {
      lines.push(`${pad}<${node.tagName}${attrs}>${escapeText(text)}</${node.tagName}>`);
    } else {
      lines.push(`${pad}<${node.tagName}${attrs}>`);
      children.forEach((c) => walk(c, depth + 1));
      lines.push(`${pad}</${node.tagName}>`);
    }
  };
  walk(doc.documentElement, 0);
  return lines.join('\n');
}

const escapeAttr = (v: string) => v.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
const escapeText = (v: string) => v.replaceAll('&', '&amp;').replaceAll('<', '&lt;');
