// Decides whether a browser download should be handed to the download manager.

export const DEFAULT_SETTINGS = {
  enabled: true,
  // 'all': catch every http(s) download. 'listed': only the extensions in `extensions`.
  mode: 'all',
  extensions: [
    'zip', 'rar', '7z', 'tar', 'gz', 'bz2', 'xz', 'exe', 'msi', 'msix', 'iso', 'img', 'bin', 'apk', 'dmg',
    'mp4', 'mkv', 'avi', 'mov', 'webm', 'wmv', 'flv', 'm4v', 'mp3', 'm4a', 'flac', 'wav', 'ogg', 'aac',
    'pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'epub', 'torrent'
  ],
  minSizeKB: 0,
  excludedHosts: [],
  showVideoButton: true
};

const NEVER_CATCH_MIME = ['text/html', 'application/xhtml+xml'];

function extOf(name) {
  const base = (name || '').split(/[\\/]/).pop().split('?')[0];
  const dot = base.lastIndexOf('.');
  return dot > 0 ? base.slice(dot + 1).toLowerCase() : '';
}

function hostMatches(host, patterns) {
  host = (host || '').toLowerCase();
  return patterns.some((p) => {
    p = p.trim().toLowerCase().replace(/^\*\./, '');
    return p && (host === p || host.endsWith('.' + p));
  });
}

/**
 * @param {chrome.downloads.DownloadItem} item
 * @param {typeof DEFAULT_SETTINGS} settings
 * @param {string} ownExtensionId downloads started by this extension are never caught
 * @returns {{catch: boolean, reason: string}}
 */
export function shouldCatch(item, settings, ownExtensionId) {
  const s = { ...DEFAULT_SETTINGS, ...settings };
  const url = item.finalUrl || item.url || '';
  if (!s.enabled) return { catch: false, reason: 'disabled' };
  if (item.byExtensionId && item.byExtensionId === ownExtensionId) return { catch: false, reason: 'own-download' };
  if (!/^https?:/i.test(url)) return { catch: false, reason: 'scheme' }; // blob:, data:, file: stay in the browser
  if (item.state && item.state !== 'in_progress') return { catch: false, reason: 'not-in-progress' };
  if (NEVER_CATCH_MIME.includes((item.mime || '').toLowerCase())) return { catch: false, reason: 'html' };

  let host = '';
  try { host = new URL(url).hostname; } catch { /* ignore */ }
  if (hostMatches(host, s.excludedHosts || [])) return { catch: false, reason: 'excluded-host' };

  if (s.mode === 'listed') {
    const ext = extOf(item.filename) || extOf(new URL(url).pathname);
    if (!s.extensions.includes(ext)) return { catch: false, reason: 'extension-not-listed' };
  }

  const size = item.totalBytes > 0 ? item.totalBytes : item.fileSize > 0 ? item.fileSize : -1;
  if (s.minSizeKB > 0 && size >= 0 && size < s.minSizeKB * 1024) return { catch: false, reason: 'too-small' };

  return { catch: true, reason: 'ok' };
}
