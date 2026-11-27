// Settings save as they change, like Chrome's own settings, with a short "Settings saved" toast.
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
  updateTypes();
}

function updateTypes() {
  // The file-type list only matters in "only these file types" mode.
  $('extensions').disabled = document.querySelector('input[name=mode]:checked').value !== 'listed';
  $('extensions').style.opacity = $('extensions').disabled ? 0.55 : 1;
}

let toastTimer = null;
function toast() {
  $('toast').classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $('toast').classList.remove('show'), 1600);
}

async function save() {
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
  toast();
}

// Switches and choices save at once; typing saves a moment after the last key.
let typing = null;
const saveSoon = () => { clearTimeout(typing); typing = setTimeout(save, 600); };
for (const id of ['enabled', 'showVideoButton']) $(id).addEventListener('change', save);
for (const r of document.querySelectorAll('input[name=mode]')) r.addEventListener('change', () => { updateTypes(); save(); });
for (const id of ['extensions', 'excludedHosts', 'minSizeKB']) {
  $(id).addEventListener('input', saveSoon);
  $(id).addEventListener('change', () => { clearTimeout(typing); save(); });
}

load();
