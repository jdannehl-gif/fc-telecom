@* Root document. Kept deliberately plain — no CDN references, no external fonts.
   A tool people open during an outage should render correctly on a connection that is,
   by definition, having a bad day. *@
<!DOCTYPE html>
<html lang="en">

<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <link rel="stylesheet" href="app.css" />
    <link rel="icon" href="data:," />
    <HeadOutlet />
</head>

<body>
    <Routes />
    <div id="blazor-error-ui">
        The connection to the server was lost.
        <a href="" class="reload">Reload</a>
        <span class="dismiss">🗙</span>
    </div>
    <script src="_framework/blazor.web.js"></script>
</body>

</html>
