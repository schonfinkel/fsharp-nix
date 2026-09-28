# Security and capability policy

## Forbidden data

Passwords, Identity tokens, raw bearer capabilities, card data, authenticator keys, provider
credentials, connection strings, and unrestricted provider or exception text must not enter FSM
state, events, actions, errors, identifiers, correlation fields, durable error columns, or logs.

Email addresses, postal addresses, invoice data, email payloads, and workflow histories are PII.
Access is least-privilege; backups must be encrypted; retention and customer erasure rules must be
defined with the feature that introduces each data set. Legally retained invoice records are an
explicit exception and must be pseudonymized where possible.

Durable and logged failures use closed, versioned internal codes. Raw exceptions may be observed
by a debugger or controlled crash dump, but ordinary application logs and health responses record
only the component, operation/correlation identifier, and approved classification.

## Capabilities

Capabilities use 256 random bits and a versioned Base64URL representation. The raw value is shown
only to the client. Persistence stores an HMAC-SHA256 digest plus key identifier, purpose, entity,
expiry, and revocation metadata. The MAC input is unambiguously framed and binds the version,
purpose, entity, and raw token; validation uses constant-time digest comparison.

Issuance uses the active key. Validation may use explicitly configured previous keys during a
bounded rotation window. Rotation or an authenticated merge revokes the old capability. Capability
issue, exchange, and use endpoints are rate-limited and return `404` for invalid, expired, revoked,
cross-purpose, cross-entity, or unauthorized values.

Bearer capabilities are exchanged once for a short-lived `HttpOnly`, `Secure`, `SameSite=Lax`
cookie before streaming or navigation. Redirects remove the token from the URL. Request logs never
include query strings, cookies, authorization headers, or form bodies. Tracking responses exclude
billing addresses, invoice data, payment references, and email addresses.

## Verification

Secret-canary tests cover every retained FSM state, event, action, and error codec. They also drive
HTTP and worker failure paths through a captured logger and fail if a secret value or forbidden
field name appears. Public health responses contain only aggregate status and stable component
codes; detailed operational aggregates require MFA.
