@* Reveals a value the caller is already authorised to see.

   The important thing this component is NOT doing: it is not hiding a value from someone
   who lacks permission. By the time a value reaches this component, the query handler has
   already checked ServiceIpData.Read and would have projected the field away otherwise —
   so an unauthorised user's page has no value to reveal, in the DOM or anywhere else.

   What this is for is the authorised user: it keeps static IP blocks off a screen that
   someone might be sharing, screenshotting, or standing next to, until they deliberately
   ask for them. The reveal was already logged as a SecurityEvent when the page loaded. *@

<span class="sensitive">
    @if (_revealed)
    {
        <code class="sensitive__value">@Value</code>
        <button type="button" class="sensitive__toggle" @onclick="Hide">Hide</button>
    }
    else
    {
        <span class="sensitive__mask" aria-label="Value hidden">••••••••••••</span>
        <button type="button" class="sensitive__toggle" @onclick="Reveal">Reveal</button>
    }
</span>

@code {
    [Parameter, EditorRequired] public string? Value { get; set; }

    private bool _revealed;

    private void Reveal() => _revealed = true;

    private void Hide() => _revealed = false;
}
