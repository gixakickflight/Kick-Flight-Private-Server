# CDN URL scheme routing

The direct-client Octo database includes a CDN URL format. For requests to `/v1/list`, the API rewrites that
format before returning the database. The fallback uses the scheme and host seen by ASP.NET.

## Observed failure

On 2026-10-11, an iOS 2.11.3 request reached `/boot/index` through the public HTTPS endpoint. The server observed
`X-Forwarded-Proto=https`, while `Request.Scheme` remained `http`. The returned Octo lists therefore contained
`http://kick-flight-fenix.us.ci/cdn/{o}` URLs. The Mac capture recorded ATS error `-1022` for those CDN URLs,
before a CDN request could reach the server. The public HTTPS sample `/cdn/VsckeT` returned HTTP 200 and matched
the catalog SHA-256 `4391193c3efd92dcef8581a57a370a0fed2fbd80eca28d9ee931935577238d3c`.

## Routing behavior

`Harness:OctoCdnUrlFormat`, when configured, remains the highest-priority override. Otherwise
`Harness:OctoCdnUrlFormatsByHost` may provide an explicit origin for a direct-client host. The production
canonical host defaults to `https://kick-flight-fenix.us.ci/cdn/{o}` in `HarnessOptions`, so it is active even
when the mounted production content root does not load the image's `/app/appsettings.json`. Explicit configuration
can still override the default. Hosts absent from this map keep the previous request-scheme and request-host
fallback, including the Android IP endpoint on port 18080.

## Validation and limits

Focused tests cover the canonical HTTPS host, the existing explicit-format override, and the Android IP/HTTP
fallback. They also verify that an untrusted `X-Forwarded-Proto` value does not change either result. The Mac
capture confirmed `X-App-Asset-Platform=2` (iOS), but the returned `/v1/list/12345` data came from the production
catalog fixture; compatibility of that catalog and its payloads with iOS remains unverified. This change has not
been deployed, and no runtime or gameplay result is claimed.
