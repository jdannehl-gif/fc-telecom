@page "/error"
@attribute [AllowAnonymous]

@* No exception detail, no stack trace, no correlation between what the user did and what
   broke. The correlation ID is enough for support to find the full detail in Application
   Insights, and it is the only thing that crosses to the browser. *@

<PageTitle>Something went wrong — FC Telecom Manager</PageTitle>

<div class="page-message page-message--error">
    <h1>Something went wrong</h1>
    <p>
        The application hit an unexpected error. It has been logged, and nothing you were
        working on has been lost — changes are only saved when you confirm them.
    </p>
    <p class="muted">
        If you need to report this, quote reference <code>@RequestId</code>.
    </p>
    <a class="button" href="/">Back to the dashboard</a>
</div>

@code {
    [CascadingParameter] private HttpContext? HttpContext { get; set; }

    private string RequestId =>
        System.Diagnostics.Activity.Current?.Id ?? HttpContext?.TraceIdentifier ?? "unavailable";
}
