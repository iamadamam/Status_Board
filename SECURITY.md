# Security policy

## Supported versions

| Version | Supported |
| ------- | --------- |
| 1.0.x   | Yes       |

## Reporting a vulnerability

Please don't open a public issue for security problems. Report them privately through the repository's **Security** tab
(*Report a vulnerability* / security advisory), with the steps to reproduce and the version you're using.

## Design notes

- Status Board makes only outbound, read-only requests. It opens no listening ports and has no auto-update or telemetry.
- TLS certificates are validated unless you explicitly allow a self-signed certificate for a specific source.
- Requests that carry credentials (LibreNMS API tokens) never follow redirects. Tokens are stored encrypted with Windows
  DPAPI for the current user.
- Responses are size-limited; XML is parsed with DTD processing and external resolution disabled; images are
  size-checked before decoding; only `http`/`https` links are opened.
- The app runs as a standard user and writes only to its own folder.
