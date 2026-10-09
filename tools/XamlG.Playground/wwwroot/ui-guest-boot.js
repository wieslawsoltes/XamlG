// This URL document has an opaque origin when embedded with sandbox=allow-scripts.
// It never starts Studio, opens IndexedDB, or receives the editor's provider credentials.
const base = new URL('.', import.meta.url).href;
const execution = document.documentElement.dataset.xamlgUiMode === 'execution';
const policy = document.createElement('meta');
policy.httpEquiv = 'Content-Security-Policy';
policy.content = `default-src 'none'; script-src 'unsafe-eval' 'wasm-unsafe-eval' ${execution ? 'blob:' : ''} ${base}; style-src 'unsafe-inline' ${base}; connect-src ${base}; img-src data: blob: ${base}; font-src data: blob: ${base}; worker-src blob: ${base}; frame-src ${execution ? new URL('ui-execution-worker.html',base).href : "'none'"}; object-src 'none'; form-action 'none'; base-uri ${base}`;
document.head.appendChild(policy);
const script = document.createElement('script');
script.src = new URL('_framework/blazor.webassembly.js', base).href;
script.setAttribute('autostart', 'false');
script.onload = async () => {
  try {
    const { runtimeOptions } = await import(new URL('startup.js', base));
    await Blazor.start(runtimeOptions(true));
  } catch (error) { document.getElementById('app').textContent = 'Native UI could not start: ' + String(error.message).slice(0,1000); }
};
script.onerror = () => { document.getElementById('app').textContent = 'Native UI runtime download failed. Use the text fallback or refresh.'; };
document.body.appendChild(script);
