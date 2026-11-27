// Picks the links worth downloading from a page, for "Download all links".

/**
 * @param {{url: string, text?: string}[]} links every <a href> on the page
 * @param {string[]} extensions file types that count as downloads (lowercase, no dot)
 * @returns {{url: string, text: string}[]} http(s) links to files, first occurrence of each, in page order
 */
export function pickFileLinks(links, extensions) {
  const wanted = new Set(extensions.map((e) => e.toLowerCase()));
  const seen = new Set();
  const out = [];
  for (const link of links) {
    let u;
    try {
      u = new URL(link.url);
    } catch {
      continue;
    }
    if (u.protocol !== 'http:' && u.protocol !== 'https:') continue;
    u.hash = '';
    const key = u.href;
    if (seen.has(key)) continue;
    const name = decodeURIComponent(u.pathname.split('/').pop() || '');
    const dot = name.lastIndexOf('.');
    const ext = dot > 0 ? name.slice(dot + 1).toLowerCase() : '';
    if (!wanted.has(ext)) continue;
    seen.add(key);
    out.push({ url: key, text: (link.text || '').trim().slice(0, 200) });
  }
  return out;
}
