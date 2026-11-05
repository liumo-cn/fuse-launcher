# Security policy

## Reporting a vulnerability

Please do not open a public issue for security problems. Send a description
and reproduction steps to **liumo_Mc@outlook.com**. You will get an
acknowledgement, and a fix or mitigation will be prepared before any public
disclosure.

## Scope

- The launcher and core library in this repository.
- The self-update path (manifest parsing, package verification).
- Anything that could lead to code execution, credential exposure, or
  arbitrary file writes outside the selected game directory.

## Out of scope

- Vulnerabilities in Minecraft itself or in third-party mods.
- Issues that require an already-compromised machine.
- The hosting infrastructure of mirrors and APIs used at runtime.
