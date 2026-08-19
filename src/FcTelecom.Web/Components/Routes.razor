@using Microsoft.AspNetCore.Components.Authorization

<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)">
            <NotAuthorized>
                @if (context.User.Identity?.IsAuthenticated != true)
                {
                    <RedirectToLogin />
                }
                else
                {
                    @* Deliberately vague. Naming the missing permission would tell a user
                       exactly which capability to go and ask for, and tells anyone who has
                       compromised the account what else exists to reach for. *@
                    <div class="page-message page-message--denied">
                        <h1>Not available</h1>
                        <p>
                            Your account does not have access to this page. If you believe you should,
                            contact your application administrator — they can see exactly which
                            permission is required.
                        </p>
                        <a href="/" class="button">Back to the dashboard</a>
                    </div>
                }
            </NotAuthorized>
            <Authorizing>
                <div class="page-message"><p>Checking your access…</p></div>
            </Authorizing>
        </AuthorizeRouteView>
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
    <NotFound>
        <LayoutView Layout="typeof(Layout.MainLayout)">
            <div class="page-message">
                <h1>Page not found</h1>
                <p>That address does not match anything in this application.</p>
                <a href="/" class="button">Back to the dashboard</a>
            </div>
        </LayoutView>
    </NotFound>
</Router>
