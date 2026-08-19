@inherits LayoutComponentBase

<div class="app-shell">
    <header class="app-header">
        <a class="app-brand" href="/">FC Telecom Manager</a>
        <GlobalSearchBox />
        <AuthorizeView>
            <Authorized>
                <div class="app-user">
                    <span class="app-user__name">@context.User.Identity?.Name</span>
                    <a class="app-user__signout" href="MicrosoftIdentity/Account/SignOut">Sign out</a>
                </div>
            </Authorized>
        </AuthorizeView>
    </header>

    <div class="app-body">
        <NavMenu />
        <main class="app-main">
            @Body
        </main>
    </div>
</div>
