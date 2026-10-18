param(
    [string] $BaseUrl = 'http://127.0.0.1:15147',
    [string] $PublicHost = 'host.howto.software',
    [switch] $CheckForwardedHttps
)

# Run only against an isolated published app/container with trials and integrations disabled.
# Uses .NET HTTP APIs so it works on Windows PowerShell and GitHub's PowerShell runner.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.BaseAddress = [uri]$BaseUrl
$client.Timeout = [TimeSpan]::FromSeconds(15)
$script:checks = 0

function Assert-Check([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}

function Send-Request([string] $Path, [bool] $Https = $false, [string] $Cookie = '', $Form = $null) {
    $method = if ($null -eq $Form) { [System.Net.Http.HttpMethod]::Get } else { [System.Net.Http.HttpMethod]::Post }
    $request = [System.Net.Http.HttpRequestMessage]::new($method, $Path)
    $request.Headers.Host = $PublicHost
    if ($Https) {
        $request.Headers.Add('X-Forwarded-For', '198.51.100.20')
        $request.Headers.Add('X-Forwarded-Proto', 'https')
    }
    if ($Cookie) { $request.Headers.Add('Cookie', $Cookie) }
    if ($null -ne $Form) { $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($Form) }
    try { return $client.SendAsync($request).GetAwaiter().GetResult() }
    finally { $request.Dispose() }
}

try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $health = Send-Request '/health'
            try { $ready = [int]$health.StatusCode -eq 200 }
            finally { $health.Dispose() }
        }
        catch { }
        if ($ready) { break }
        Start-Sleep -Seconds 1
    }
    Assert-Check $ready 'The isolated published application did not become healthy.'

    # Repeat without a browser cache to catch hard-refresh and generated-asset regressions.
    for ($refresh = 0; $refresh -lt 2; $refresh++) {
        $page = Send-Request '/'
        try {
            Assert-Check ([int]$page.StatusCode -eq 200) 'The production hostname must serve the homepage.'
            $html = $page.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $csp = $page.Headers.GetValues('Content-Security-Policy') -join ' '
            Assert-Check (-not $csp.Contains('upgrade-insecure-requests')) 'HTTP must not upgrade assets to unavailable TLS.'
            Assert-Check ($csp.Contains("object-src 'none'") -and $csp.Contains("frame-ancestors 'none'")) 'The document must retain its restrictive CSP.'
            $assets = @([regex]::Matches($html, '(?:src|href)="([^"]+)"') |
                ForEach-Object { [System.Net.WebUtility]::HtmlDecode($_.Groups[1].Value) } |
                Where-Object { $_ -match '^/?(?:_framework/|js/|css/|fonts/|HowToSoftware\.Hosting\..*\.styles\.css)' } |
                Sort-Object -Unique)
            Assert-Check ($assets.Count -ge 9) 'Critical fingerprinted application assets are missing from the HTML.'
            foreach ($path in $assets) {
                Assert-Check (-not $path.StartsWith('//') -and $path -notmatch '^[a-z]+:') 'Application assets must use same-origin paths.'
                $asset = Send-Request ('/' + $path.TrimStart('/'))
                try {
                    Assert-Check ([int]$asset.StatusCode -eq 200) "Published asset failed: $path"
                    $type = $asset.Content.Headers.ContentType.MediaType
                    Assert-Check ($type -ne 'text/html') "Published asset returned an HTML fallback: $path"
                }
                finally { $asset.Dispose() }
            }
        }
        finally { $page.Dispose() }
    }

    foreach ($path in @('/game-hosting', '/game-hosting/project-zomboid', '/game-hosting/minecraft', '/infrastructure', '/trial')) {
        $page = Send-Request $path
        try { Assert-Check ([int]$page.StatusCode -eq 200) "Public route failed: $path" }
        finally { $page.Dispose() }
    }

    $transports = @($false)
    if ($CheckForwardedHttps) { $transports += $true }
    foreach ($https in $transports) {
        $page = Send-Request '/trial' $https
        try {
            Assert-Check ([int]$page.StatusCode -eq 200) 'The trial form must render in Production.'
            $html = $page.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $token = [regex]::Match($html, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
            Assert-Check $token.Success 'The trial form must have an antiforgery token.'
            $cookies = @($page.Headers.GetValues('Set-Cookie') | Where-Object { $_ -match '^[^=]*Antiforgery[^=]*=' })
            Assert-Check ($cookies.Count -eq 1) 'The trial form must issue one antiforgery cookie.'
            $cookie = $cookies[0]
            $secure = $cookie -match '(?i);\s*secure(?:;|$)'
            Assert-Check ($secure -eq $https) 'The antiforgery cookie must follow the effective request transport.'
            Assert-Check (-not $cookie.StartsWith('__Host-') -or $secure) 'A browser would reject this __Host- cookie without Secure.'
            $csp = $page.Headers.GetValues('Content-Security-Policy') -join ' '
            Assert-Check ($csp.Contains('upgrade-insecure-requests') -eq $https) 'CSP must follow the trusted request transport.'
            if ($https) {
                Assert-Check ($page.Headers.Contains('Strict-Transport-Security')) 'HTTPS production responses must include HSTS.'
                Assert-Check ($csp.Contains("wss://$PublicHost")) 'HTTPS must authorize the same-origin Blazor WebSocket.'
            }
            $form = [System.Collections.Generic.Dictionary[string,string]]::new()
            $form.Add('Email', 'smoke@example.test')
            $form.Add('__RequestVerificationToken', [System.Net.WebUtility]::HtmlDecode($token.Groups[1].Value))
            $rejected = Send-Request '/trials/recover' $https '' $form
            try { Assert-Check ([int]$rejected.StatusCode -eq 400) 'A form token alone must not bypass the antiforgery cookie.' }
            finally { $rejected.Dispose() }
            $accepted = Send-Request '/trials/recover' $https ($cookie.Split(';')[0]) $form
            try {
                Assert-Check ([int]$accepted.StatusCode -eq 302) 'A valid antiforgery cookie/token pair must reach the handler.'
                Assert-Check ($accepted.Headers.Location.OriginalString -eq '/trial?notice=unavailable') 'Smoke tests require disabled trials and external integrations.'
            }
            finally { $accepted.Dispose() }
        }
        finally { $page.Dispose() }
    }
    Write-Output "Production smoke checks passed: $script:checks (published assets, routes, refresh, forms and transport policy)."
}
finally { $client.Dispose() }
