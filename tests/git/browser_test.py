"""Real Chromium/IndexedDB acceptance tests for the Git workbench JS host.

Run: python tests/git/browser_test.py
Requires Playwright's Python package and Chromium (CHROMIUM_PATH overrides detection).
This exercises the shipped workbench, not the Razor/Dockyard build.
"""
from contextlib import contextmanager
from pathlib import Path
import json, os, shutil, time, mimetypes
from urllib.parse import urlparse, unquote
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / 'test-results' / 'git-browser'
ARTIFACTS.mkdir(parents=True, exist_ok=True)
results = []
@contextmanager
def check(name):
    started = time.monotonic()
    try:
        yield
        results.append({'name': name, 'passed': True, 'seconds': round(time.monotonic() - started, 3)})
    except Exception as error:
        results.append({'name': name, 'passed': False, 'error': str(error)})
        raise

def idle(page):
    expect(page.locator('[aria-busy="true"]')).to_have_count(0)

def saved_text(page, path, layer='worktree'):
    return page.evaluate('''async ({path, layer}) => {
      const {WorkspaceStore} = await import('/tools/XamlG.Playground/wwwroot/git/storage.mjs');
      const store = new WorkspaceStore(); const state = (await store.list())[0];
      const entry = state[layer].get(path); const bytes = entry ? await store.blob(entry.oid) : null;
      await store.close(); return bytes ? new TextDecoder('utf-8', {ignoreBOM:true}).decode(bytes) : null;
    }''', {'path': path, 'layer': layer})

try:
    with sync_playwright() as p:
        chromium = os.environ.get('CHROMIUM_PATH') or shutil.which('chromium') or shutil.which('chromium-browser')
        browser = p.chromium.launch(**({'executable_path': chromium} if chromium else {}), headless=True, args=['--no-sandbox'])
        context = browser.new_context(viewport={'width': 1600, 'height': 1050})
        page = context.new_page(); errors = []; page.on('pageerror', lambda error: errors.append(str(error)))
        page.on('dialog', lambda dialog: dialog.accept(dialog.default_value) if dialog.type == 'prompt' else dialog.accept())
        # Fulfill every resource from this source tree. No browser network connection,
        # local server access, GitHub account or remote credentials are involved.
        def fixture(route):
            url = urlparse(route.request.url)
            path = (ROOT / unquote(url.path).lstrip('/')).resolve()
            if url.hostname != 'xamlg-workbench.test' or not path.is_relative_to(ROOT) or not path.is_file():
                route.abort(); return
            mime = 'text/javascript' if path.suffix in ['.mjs', '.js'] else mimetypes.guess_type(path)[0] or 'application/octet-stream'
            route.fulfill(status=200, body=path.read_bytes(), content_type=mime)
        context.route('**/*', fixture)
        with check('Offline Chromium fixture loads'):
            page.goto('https://xamlg-workbench.test/tests/git/browser.html'); page.wait_for_function('window.harnessReady')
        repositories = page.locator('#repositories'); changes = page.locator('#changes'); documents = page.locator('#document')
        with check('Create persistent virtual repository and working file through UI'):
            repositories.get_by_label('Workspace name', exact=True).fill('Browser acceptance')
            repositories.get_by_role('button', name='New virtual', exact=True).click(); idle(page)
            repositories.get_by_label('New file path', exact=True).fill('View.axaml')
            repositories.get_by_role('button', name='Create file', exact=True).click(); idle(page)
            editor = documents.get_by_label('View.axaml Git editor', exact=True)
            expect(editor).to_be_visible()
            editor.fill('<TextBlock Text="Initial" />\n')
            documents.get_by_role('button', name='Save', exact=True).click(); idle(page)
            assert saved_text(page, 'View.axaml') == '<TextBlock Text="Initial" />\n'
        with check('Stage and commit preserve independent snapshots'):
            documents.get_by_role('button', name='Stage saved file', exact=True).click(); idle(page)
            editor.fill('<TextBlock Text="Unstaged" />\n')
            documents.get_by_role('button', name='Save', exact=True).click(); idle(page)
            assert saved_text(page, 'View.axaml', 'index') == '<TextBlock Text="Initial" />\n'
            changes.get_by_label('Author name', exact=True).fill('Browser Test')
            changes.get_by_label('Author email', exact=True).fill('browser@example.test')
            changes.get_by_label('Commit message', exact=True).fill('Initial browser commit')
            changes.get_by_role('button', name='Commit staged', exact=True).click(); idle(page)
            assert saved_text(page, 'View.axaml', 'head') == '<TextBlock Text="Initial" />\n'
            assert saved_text(page, 'View.axaml') == '<TextBlock Text="Unstaged" />\n'
        with check('Close/reopen document retains dirty editor buffer'):
            editor.fill('<TextBlock Text="Unsaved" />\n')
            page.get_by_role('button', name='Close active document').click()
            repositories.get_by_role('button', name='View.axaml', exact=True).click(); idle(page)
            editor = documents.get_by_label('View.axaml Git editor', exact=True)
            expect(editor).to_have_value('<TextBlock Text="Unsaved" />\n')
            assert saved_text(page, 'View.axaml') == '<TextBlock Text="Unstaged" />\n'
        with check('Explicit source transfer roundtrips through host'):
            documents.get_by_role('button', name='Copy into Studio', exact=True).click(); idle(page)
            assert page.evaluate('window.studioSources["View.axaml"]') == '<TextBlock Text="Unsaved" />\n'
            page.evaluate('window.studioSources["View.axaml"] = \'<TextBlock Text="Studio edit" />\\n\'')
            documents.get_by_role('button', name='Capture Studio edits', exact=True).click(); idle(page)
            expect(documents.get_by_label('View.axaml Git editor', exact=True)).to_have_value('<TextBlock Text="Studio edit" />\n')
            documents.get_by_role('button', name='Save', exact=True).click(); idle(page)
            assert saved_text(page, 'View.axaml') == '<TextBlock Text="Studio edit" />\n'
        with check('Selective hunk staging through diff document'):
            base = ''.join(f'line {i}\n' for i in range(25))
            repositories.get_by_label('New file path', exact=True).fill('two-hunks.txt')
            repositories.get_by_role('button', name='Create file', exact=True).click(); idle(page)
            ed = documents.get_by_label('two-hunks.txt Git editor', exact=True)
            ed.fill(base); documents.get_by_role('button', name='Save', exact=True).click(); idle(page)
            documents.get_by_role('button', name='Stage saved file', exact=True).click(); idle(page)
            updated = base.replace('line 1\n','first hunk\n').replace('line 23\n','second hunk\n')
            ed.fill(updated); documents.get_by_role('button', name='Save', exact=True).click(); idle(page)
            documents.get_by_role('button', name='Diff', exact=True).click(); idle(page)
            expect(documents.get_by_role('checkbox')).to_have_count(2)
            documents.get_by_label('Stage hunk 1', exact=True).check()
            documents.get_by_role('button', name='Stage selected hunks', exact=True).click(); idle(page)
            assert saved_text(page, 'two-hunks.txt', 'index') == base.replace('line 1\n', 'first hunk\n')
            assert saved_text(page, 'two-hunks.txt') == updated
        with check('IndexedDB workspace survives reload without credentials'):
            page.reload(); page.wait_for_function('window.harnessReady')
            repositories.get_by_role('button', name='View.axaml', exact=True).wait_for()
            assert saved_text(page, 'View.axaml') == '<TextBlock Text="Studio edit" />\n'
            assert saved_text(page, 'two-hunks.txt', 'index').startswith('line 0\nfirst hunk\n')
            snapshot = page.evaluate('''async () => { const {WorkspaceStore} = await import('/tools/XamlG.Playground/wwwroot/git/storage.mjs'); const s = new WorkspaceStore(); const state=(await s.list())[0]; await s.close();return [...Object.keys(state)]; }''')
            assert 'token' not in snapshot and 'credentials' not in snapshot
        with check('History and changed-file document navigation'):
            page.locator('#history').get_by_role('button', name='Initial browser commit', exact=False).click(); idle(page)
            expect(documents.locator('pre').first).to_contain_text('Initial browser commit')
            documents.get_by_role('button', name='added · View.axaml', exact=True).click(); idle(page)
            expect(documents.locator('pre')).to_contain_text('+<TextBlock Text="Initial" />')
        with check('Repository names and paths are rendered as text, not HTML'):
            repositories.get_by_label('Workspace name', exact=True).fill('<img src=x onerror="window.injected=1">')
            repositories.get_by_role('button', name='New virtual', exact=True).click(); idle(page)
            assert not page.evaluate('window.injected')
            expect(repositories.locator('img')).to_have_count(0)
        with check('No uncaught browser errors'):
            assert errors == [], errors
        page.screenshot(path=str(ARTIFACTS / 'workbench.png'), full_page=True)
        context.close(); browser.close()
finally:
    (ARTIFACTS / 'results.json').write_text(json.dumps(results, indent=2))
    print(json.dumps(results, indent=2))
