@inject NavigationManager Navigation

@code {
    // Sends an unauthenticated visitor to the Entra sign-in flow, preserving where they
    // were trying to go. Someone who follows a deep link to a circuit during an outage
    // should land on that circuit after signing in, not on the dashboard.
    protected override void OnInitialized()
    {
        string returnUrl = Uri.EscapeDataString(
            Navigation.ToBaseRelativePath(Navigation.Uri));

        Navigation.NavigateTo(
            $"MicrosoftIdentity/Account/SignIn?redirectUri=/{returnUrl}",
            forceLoad: true);
    }
}
