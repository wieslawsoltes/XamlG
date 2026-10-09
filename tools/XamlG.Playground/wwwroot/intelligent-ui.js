export async function copyText(text) {
  if (typeof text !== 'string' || text.length > 131072) throw new Error('Copy text exceeds its limit.');
  if (!navigator.clipboard) throw new Error('Clipboard access is unavailable in this browser context.');
  await navigator.clipboard.writeText(text);
}
export function openLink(value) {
  const url = new URL(value);
  if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password) throw new Error('Only absolute HTTP(S) links without credentials are allowed.');
  window.open(url.href, '_blank', 'noopener,noreferrer');
}
export function downloadText(name, mime, text) {
  if (!/^[a-zA-Z0-9_.-]{1,100}$/.test(name) || typeof text !== 'string' || text.length > 4194304 || !['application/xml', 'application/json', 'text/plain'].includes(mime)) throw new Error('Invalid export.');
  const url = URL.createObjectURL(new Blob([text], {type: mime + ';charset=utf-8'}));
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = name; anchor.rel = 'noopener';
  document.body.append(anchor); anchor.click(); anchor.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
}
