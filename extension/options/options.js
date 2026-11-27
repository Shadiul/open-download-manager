const $ = (id) => document.getElementById(id);
const split = (text) => text.split(/[\s,]+/).map((s) => s.trim().replace(/^\./, '').toLowerCase()).filter(Boolean);

async function load() {
  const s = await chrome.runtime.sendMessage({ type: 'get-settings' });
  $('enabled').checked = s.enabled;
  document.querySelector(`input[name=mode][value=${s.mode}]`).checked = true;
  $('extensions').value = s.extensions.join(' ');
  $('minSizeKB').value = s.minSizeKB;
  $('excludedHosts').value = s.excludedHosts.join('\n');
  $('showVideoButton').checked = s.showVideoButton;
}

$('save').addEventListener('click', async () => {
  await chrome.runtime.sendMessage({
    type: 'set-settings',
    settings: {
      enabled: $('enabled').checked,
      mode: document.querySelector('input[name=mode]:checked').value,
      extensions: split($('extensions').value),
      minSizeKB: Math.max(0, Number($('minSizeKB').value) || 0),
      excludedHosts: $('excludedHosts').value.split('\n').map((s) => s.trim()).filter(Boolean),
      showVideoButton: $('showVideoButton').checked
    }
  });
  $('saved').hidden = false;
  setTimeout(() => { $('saved').hidden = true; }, 1500);
});

load();
