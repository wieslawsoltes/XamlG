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
  await Blazor.start({
    configureRuntime: dotnet => dotnet.withConfig({ maxParallelDownloads: 8, enableDownloadRetry: true })
  });
}
