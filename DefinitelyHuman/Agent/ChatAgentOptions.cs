namespace DefinitelyHuman.Agent;

public struct ChatAgentOptions
{
    public string ApiKey { get; set; }
    
    public string Model { get; set; }
    
    public string Nick { get; set; }
    
    /// <summary>
    /// If true, the agent will log its "thinking" (reasoning, plans, thoughts, etc) to the console and dashboard.
    /// This is separate from the decision log, which is always captured and shown on the dashboard.
    /// </summary>
    public bool EnableThinking { get; set; }

    /// <summary>
    /// If true, the agent will echo its decision log + thinking to the console. The dashboard log is captured regardless.
    /// </summary>
    public bool LogReasoning { get; set; }
}