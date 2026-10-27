# Security policy

## Supported versions

Only the latest release tag receives security fixes. The moving image tags (`X.Y` and `latest`)
follow that release, so pulling them again is how a fix reaches a running site. Older tags are not
patched; upgrade to the latest release.

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub private vulnerability reporting:
<https://github.com/stevedcc/croft/security/advisories/new>

Do not open a public issue, pull request or discussion for a vulnerability.

## What to expect

Croft is a one-maintainer project. I aim to acknowledge a report within 7 days. After that I will
confirm whether I can reproduce it, work on a fix, and publish a release and an advisory; how long
that takes depends on the severity and on my availability, and I will say so in the report thread.

## Scope

In scope: the code in this repository and the container images built from it.

Out of scope: a site that runs Croft. Report problems with a particular site (its content,
configuration, hosting or identity provider) to that site's owner.
