namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// The alias table between the tool names a Langflow flow registers and the eleven
/// names the API contract uses.
///
/// <para>
/// <c>langflow/planning-agent.v3.baseline.json</c> wires its Agent to components
/// display-named <c>Save Task Tool</c> and <c>Update Task Tool</c> — Langflow reports
/// them as <c>SaveTaskTool</c> / <c>UpdateTaskTool</c>, which are not
/// <c>createTask</c> / <c>updateTask</c>. The chat UI renders a pill per
/// <c>tool_call</c> and labels it from <c>name</c>, so an unmapped name shows the
/// component's name instead of the action.
/// </para>
///
/// <para>
/// <b>An unknown name is passed through, not rejected.</b> Node aborts the turn with
/// <c>unknown_tool</c> when Gemini names a tool outside <c>TOOL_NAMES</c>, because
/// there it means the model hallucinated a function that does not exist. Here the
/// opposite is true: the flow really does own tools we have no name for yet, a
/// sibling is renaming them right now, and killing a turn over a label would make
/// every Langflow answer fail. The pill degrades to the raw name;
/// <c>needsConfirmation</c> stays false, which is correct because the only name that
/// ever gates is <c>deleteAllTasks</c> and it is spelled explicitly.
/// </para>
///
/// <para>Matching is case-insensitive and ignores spaces and underscores, so
/// <c>Save Task Tool</c>, <c>save_task_tool</c> and <c>SaveTaskTool</c> all land on the
/// same entry.</para>
/// </summary>
public static class LangflowToolNames
{
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["savetasktool"] = "createTask",
            ["createtasktool"] = "createTask",
            ["updatetasktool"] = "updateTask",
            ["completetasktool"] = "completeTask",
            ["deletetasktool"] = "deleteTask",
            ["deletealltaskstool"] = AiToolCatalog.DeleteAllTasks,
            ["querytaskstool"] = "queryTasks",
            ["snoozetasktool"] = "snoozeTask",
            ["holdforclarificationtool"] = "holdForClarification",
        };

    /// <summary>Langflow's component name → the contract's tool name, or the input unchanged.</summary>
    public static string ToContractName(string langflowName)
    {
        if (string.IsNullOrEmpty(langflowName))
        {
            return langflowName;
        }

        // Already one of ours — the flow may well emit the contract names directly,
        // and that path must not be routed through the alias table.
        if (AiToolCatalog.IsKnownTool(langflowName))
        {
            return langflowName;
        }

        return Aliases.TryGetValue(Normalize(langflowName), out var mapped) ? mapped : langflowName;
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
