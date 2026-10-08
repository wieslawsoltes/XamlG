const base = new URL('.', import.meta.url);
const modules = new Map();
const moduleNames = new Set(['studio.js', 'csharp-language.js', 'source-buffer.js']);
const pause = attempt => new Promise(resolve => setTimeout(resolve, 500 * (attempt + 1)));

class ModuleLoadError extends Error { }

// Every consumer shares the successful module URL, including after a failed
// import has been cached by the browser. Dependencies use the same bounded loader.
export function importModule(name) {
  if (!moduleNames.has(name)) throw new Error('Unknown Studio module.');
  let pending = modules.get(name);
  if (!pending) {
    pending = loadModule(name).catch(error => {
      if (modules.get(name) === pending) modules.delete(name);
      throw error;
    });
    modules.set(name, pending);
  }
  return pending;
}

async function loadModule(name) {
  for (let attempt = 0; ; attempt++) {
    const url = new URL(name, base);
    if (attempt) url.searchParams.set('startup-retry', String(attempt));
    try { return await import(url.href); }
    catch (error) {
      // A dependency already used its own attempts. Do not multiply retries
      // through the module graph or retry syntax/evaluation errors indefinitely.
      if (error instanceof ModuleLoadError) throw error;
      if (!(error instanceof TypeError) || attempt >= 2)
        throw new ModuleLoadError(`Could not load ${name}. Reload Studio to try again.`, { cause: error });
      await pause(attempt);
    }
  }
}

async function loadBlazor() {
  for (let attempt = 0; ; attempt++) {
    const script = document.createElement('script');
    const url = new URL('_framework/blazor.webassembly.js', base);
    if (attempt) url.searchParams.set('startup-retry', String(attempt));
    script.src = url.href;
    script.setAttribute('autostart', 'false');
    try {
      await new Promise((resolve, reject) => {
        script.onload = resolve;
        script.onerror = () => reject(new Error('The browser runtime loader could not be downloaded.'));
        document.body.appendChild(script);
      });
      return;
    } catch (error) {
      script.remove();
      if (attempt >= 2) throw error;
      await pause(attempt);
    }
  }
}

export async function startStudio() {
  globalThis.xamlgBoot = { importModule };
  await Promise.all([loadBlazor(), importModule('studio.js')]);
  await Blazor.start(runtimeOptions());
}

export function runtimeOptions(isolated = false) {
  return {
    // Retry inside the resource promise: the native retry path exposes failed
    // intermediate fetch promises as unhandled browser errors. Keep one policy.
    configureRuntime: dotnet => dotnet.withConfig({ maxParallelDownloads: 8, enableDownloadRetry: false }),
    loadBootResource: (type, name, uri, integrity) => type === 'dotnetjs' ? uri : downloadResource(uri, {
      credentials: isolated ? 'omit' : 'same-origin', cache: isolated ? 'no-store' : 'no-cache', integrity
    })
  };
}

async function downloadResource(uri, options) {
  for (let attempt = 0; ; attempt++) {
    try {
      const response = await fetch(uri, options);
      if (response.ok) return response;
      await response.body?.cancel();
      const error = new Error(`Could not download a runtime resource (HTTP ${response.status}).`);
      error.retryable = [408, 429, 500, 502, 503, 504].includes(response.status);
      throw error;
    } catch (error) {
      // Fetch rejects network and integrity failures with TypeError. Integrity
      // remains browser-enforced on every attempt; invalid bytes never execute.
      if (attempt >= 2 || !(error instanceof TypeError || error.retryable)) throw error;
      await pause(attempt);
    }
  }
}
