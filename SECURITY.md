# Security Policy

## Supported versions

| Version | Supported |
| --- | --- |
| 1.0.0-rc.x | Security fixes considered for the active RC line |
| `< 1.0.0` (0.x) | Best-effort only; upgrade to the current RC/GA |

Once `1.0.0` ships, the latest patch on the current major/minor receives security fixes. Older majors are unsupported unless announced otherwise.

## HTTP / SSRF

Sonora can load `http`/`https` audio via `HttpAudioSource`. **Applications are responsible for SSRF and egress controls** when accepting arbitrary URLs from users or untrusted input. Sonora does not sandbox destinations, block link-local/metadata addresses, or enforce an allowlist. Prefer application-level URL validation, private-network blocks, and authenticated outbound proxies as appropriate.

Sensitive URL query parameters and `Authorization` headers are not logged by Sonora; `Authorization` is stripped on cross-origin redirects. Do not treat that as a complete security boundary.

## Reporting a vulnerability

Please report suspected security issues privately (do not open a public GitHub issue with exploit details):

1. Email the maintainers listed in the repository / NuGet package authors, or use GitHub Security Advisories if enabled for the repo.
2. Include Sonora package versions, OS, and a minimal reproduction when possible.
3. Allow reasonable time for a fix before public disclosure.

We will acknowledge reports and coordinate fixes for supported versions.
