// Talks to the desktop download manager through Chrome native messaging.
// Protocol: see native-host/PROTOCOL.md in the repository.

export const HOST_NAME = 'org.opendownloadmanager.host';
export const PROTOCOL_VERSION = 1;

/** Sends one message and resolves with the host's reply. Rejects if the host is missing or errors. */
export function sendToHost(message, timeoutMs = 8000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Open Download Manager did not answer in time')), timeoutMs);
    try {
      chrome.runtime.sendNativeMessage(HOST_NAME, { v: PROTOCOL_VERSION, ...message }, (reply) => {
        clearTimeout(timer);
        const err = chrome.runtime.lastError;
        if (err) return reject(new Error(err.message));
        if (!reply) return reject(new Error('Empty reply from Open Download Manager'));
        resolve(reply);
      });
    } catch (e) {
      clearTimeout(timer);
      reject(e);
    }
  });
}

export async function pingHost() {
  try {
    const reply = await sendToHost({ type: 'ping' }, 4000);
    // `running: false` means the host answered for an app that is installed but not started yet.
    return { ok: !!reply.ok, running: reply.running !== false, version: reply.version || null, error: reply.error || null };
  } catch (e) {
    return { ok: false, running: false, version: null, error: e.message };
  }
}
