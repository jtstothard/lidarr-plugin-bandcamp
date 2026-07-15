# Security Policy

## Supported Versions

Only the latest release is supported. Please update to the newest version before reporting a security issue.

## Reporting a Vulnerability

This plugin handles Bandcamp session cookies (long-lived authentication tokens), so please report security issues privately rather than filing a public GitHub issue.

Use [GitHub's private vulnerability reporting](https://github.com/jtstothard/lidarr-plugin-bandcamp/security/advisories/new) for this repository. This opens a private draft advisory visible only to the maintainer until a fix is ready.

If you're unable to use that, open a regular issue asking for a private contact channel without describing the vulnerability, and the maintainer will follow up.

Please include:

- A description of the issue and its potential impact
- Steps to reproduce (plugin version, Lidarr version/branch, minimal repro if possible)
- Any relevant logs — **redact your `identity` cookie value and any other credentials before sharing logs**

## Scope

Examples of in-scope issues:

- Cookie/credential values being logged, cached, or exposed in error messages
- Cookie or credential values sent to any destination other than `bandcamp.com`
- Vulnerabilities in request handling that could leak session data
- Dependency vulnerabilities with a realistic exploit path in this plugin

Out of scope:

- Vulnerabilities in Lidarr itself (report to [Lidarr/Lidarr](https://github.com/Lidarr/Lidarr))
- Vulnerabilities in Bandcamp's own site or API
- Issues requiring an attacker to already have local access to the machine running Lidarr

## Response

The maintainer will acknowledge reports as soon as possible on a best-effort basis. This is a solo-maintained open source project without a fixed SLA.
