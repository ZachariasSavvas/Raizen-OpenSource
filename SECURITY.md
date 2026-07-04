# Security Policy

Raizen is privileged endpoint administration software. Please do not publish
working exploit details for a vulnerability until maintainers have had a
reasonable chance to investigate and prepare a fix.

## Reporting Vulnerabilities

Report suspected vulnerabilities privately to the project maintainer before
opening a public issue. Include:

- affected version or commit
- affected component
- reproduction steps
- expected and actual security impact
- logs or screenshots with secrets removed

## Security Expectations

Production deployments should use unique secrets, TLS, endpoint API keys, admin
authentication, audit logging, and poll-response signing. Development defaults
are for local testing only.
