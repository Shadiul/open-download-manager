// Switched per browser by scripts/build.mjs. The Chrome Web Store does not accept extensions that
// download YouTube videos, so the Chrome build turns YouTube support off. The Edge build keeps it.
export const BUILD_TARGET = 'edge';
export const YOUTUBE_ENABLED = true;
