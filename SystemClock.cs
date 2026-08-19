@* Status is never conveyed by colour alone.
   Every chip carries an icon, a word, and a colour. About 8% of men have some form of
   colour vision deficiency, and beyond accessibility, this application gets used on a
   phone in a dim wiring closet where a red/green distinction is genuinely hard to make. *@

<span class="chip chip--@CssClass" title="@Tooltip">
    <span class="chip__icon" aria-hidden="true">@Icon</span>
    <span class="chip__label">@Label</span>
</span>

@code {
    // Named State/Status rather than MonitorState/ServiceStatus so the parameter names do
    // not shadow the enum type names inside this component's own code block.
    [Parameter] public MonitorState? State { get; set; }

    [Parameter] public ServiceStatus? Status { get; set; }

    [Parameter] public string? Tooltip { get; set; }

    private string Icon => State switch
    {
        MonitorState.Up => "●",
        MonitorState.Down => "●",
        MonitorState.Suspect => "◐",
        MonitorState.Recovering => "◑",
        MonitorState.Unknown => "?",
        _ => Status switch
        {
            ServiceStatus.Active => "●",
            ServiceStatus.Disconnected => "⊘",
            ServiceStatus.Suspended => "⏸",
            ServiceStatus.PendingDisconnect => "⏳",
            _ => "○",
        },
    };

    private string Label => State switch
    {
        MonitorState.Up => "Up",
        MonitorState.Down => "Down",
        MonitorState.Suspect => "Suspect",
        MonitorState.Recovering => "Recovering",
        MonitorState.Unknown => "Unknown",
        _ => Status?.ToString() ?? "—",
    };

    private string CssClass => State switch
    {
        MonitorState.Up => "up",
        MonitorState.Down => "down",
        MonitorState.Suspect or MonitorState.Recovering => "warn",
        // Unknown gets its own neutral treatment, never the green of "up". A circuit with
        // no monitoring coverage must not look healthy at a glance.
        MonitorState.Unknown => "unknown",
        _ => Status switch
        {
            ServiceStatus.Active => "up",
            ServiceStatus.Disconnected => "muted",
            ServiceStatus.Suspended or ServiceStatus.PendingDisconnect => "warn",
            _ => "neutral",
        },
    };
}
