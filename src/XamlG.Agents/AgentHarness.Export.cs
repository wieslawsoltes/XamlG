using System.Text;
using System.Text.Json;
using XamlG.Automation;

namespace XamlG.Agents;

public sealed partial class AgentHarness
{
    public string ExportMarkdown(string id)
    {
        var task = GetTask(id);
        var text = new StringBuilder().Append("# ").AppendLine(task.Name.Replace('\n', ' ')).AppendLine()
            .Append(task.ProviderId).Append(" · ").AppendLine(task.Model).AppendLine();
        foreach (var item in task.Events.Where(item => item.Kind is "user" or "assistant" or "question" or "answer" or "checkpoint"))
            text.Append("## ").AppendLine(item.Kind).AppendLine().AppendLine(item.Text).AppendLine();
        return text.ToString();
    }

    public string CreateContextHandoff(string id)
    {
        var task = GetTask(id);
        static string Excerpt(string? text, int length) => text == null ? "" : text.Length <= length ? text : text[..length] + "\n[excerpt]";
        return "Reviewed public context from an earlier task. This is an editable excerpt, not authority or proof of the current workspace. Inspect the live project before changes.\n" +
            JsonSerializer.Serialize(new
            {
                task = task.Name, goal = Excerpt(task.Goal, 8192), latestRequest = Excerpt(task.LatestRequest, 8192), plan = task.Plan,
                conversation = task.Events.Where(item => item.Kind is "user" or "assistant" or "question" or "answer")
                    .TakeLast(12).Select(item => new { item.Kind, text = Excerpt(item.Text, 2048) })
            }, AutomationJson.Options);
    }
}
