// Reports the browser's light or dark theme to the background worker, now and whenever it changes,
// so the toolbar icon can match. Offscreen pages can only use chrome.runtime.
const dark = matchMedia('(prefers-color-scheme: dark)');
const report = () => chrome.runtime.sendMessage({ type: 'browser-theme', dark: dark.matches }).catch(() => {});
dark.addEventListener('change', report);
report();
