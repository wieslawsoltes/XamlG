using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace XamlG.Mcp;

internal static class AutomationMcpPagination
{
    private static readonly ConditionalWeakTable<McpServerOptions, object> Installed = new();
    public static void Attach(McpServerOptions options)
    {
        lock (Installed)
        {
            if (Installed.TryGetValue(options, out _)) return;
            Installed.Add(options, new());
            // An embedding host's explicit listing handler owns its own pagination.
            if (options.Handlers.ListToolsHandler == null)
                options.Filters.Request.ListToolsFilters.Add(next => async (request, token) =>
                {
                    if (options.Handlers.ListToolsHandler != null) return await next(request, token);
                    request.Params ??= new(); var cursor = request.Params.Cursor; request.Params.Cursor = null;
                    ListToolsResult result;
                    try { result = await next(request, token); } finally { request.Params.Cursor = cursor; }
                    (result.Tools, result.NextCursor) = Page(result.Tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray(), cursor, "tools");
                    return result;
                });
            if (options.Handlers.ListResourcesHandler == null)
                options.Filters.Request.ListResourcesFilters.Add(next => async (request, token) =>
                {
                    if (options.Handlers.ListResourcesHandler != null) return await next(request, token);
                    request.Params ??= new(); var cursor = request.Params.Cursor; request.Params.Cursor = null;
                    ListResourcesResult result;
                    try { result = await next(request, token); } finally { request.Params.Cursor = cursor; }
                    (result.Resources, result.NextCursor) = Page(result.Resources.OrderBy(resource => resource.Uri, StringComparer.Ordinal).ToArray(), cursor, "resources");
                    return result;
                });
            if (options.Handlers.ListResourceTemplatesHandler == null)
                options.Filters.Request.ListResourceTemplatesFilters.Add(next => async (request, token) =>
                {
                    if (options.Handlers.ListResourceTemplatesHandler != null) return await next(request, token);
                    request.Params ??= new(); var cursor = request.Params.Cursor; request.Params.Cursor = null;
                    ListResourceTemplatesResult result;
                    try { result = await next(request, token); } finally { request.Params.Cursor = cursor; }
                    (result.ResourceTemplates, result.NextCursor) = Page(result.ResourceTemplates.OrderBy(resource => resource.UriTemplate, StringComparer.Ordinal).ToArray(), cursor, "templates");
                    return result;
                });
            if (options.Handlers.ListPromptsHandler == null)
                options.Filters.Request.ListPromptsFilters.Add(next => async (request, token) =>
                {
                    if (options.Handlers.ListPromptsHandler != null) return await next(request, token);
                    request.Params ??= new(); var cursor = request.Params.Cursor; request.Params.Cursor = null;
                    ListPromptsResult result;
                    try { result = await next(request, token); } finally { request.Params.Cursor = cursor; }
                    (result.Prompts, result.NextCursor) = Page(result.Prompts.OrderBy(prompt => prompt.Name, StringComparer.Ordinal).ToArray(), cursor, "prompts");
                    return result;
                });
        }
    }
    private static (IList<T> Items, string? Cursor) Page<T>(T[] items, string? cursor, string category)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(items)))[..24];
        var offset = 0;
        if (cursor != null)
        {
            try
            {
                if (cursor.Length > 192) throw new FormatException();
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
                if (parts.Length != 3 || parts[0] != category || parts[2] != fingerprint ||
                    !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset >= items.Length) throw new FormatException();
            }
            catch (FormatException) { throw new McpProtocolException("Invalid or stale catalog cursor. Restart the list request.", McpErrorCode.InvalidParams); }
        }
        const int size = 100;
        return (items.Skip(offset).Take(size).ToArray(), offset + size >= items.Length ? null :
            Convert.ToBase64String(Encoding.UTF8.GetBytes(category + ":" + (offset + size).ToString(CultureInfo.InvariantCulture) + ":" + fingerprint)));
    }
}
