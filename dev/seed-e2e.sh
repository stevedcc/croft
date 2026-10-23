#!/usr/bin/env bash
set -euo pipefail

# E2E baseline seed — intentionally empty.
#
# This is the only place for data the whole E2E suite genuinely shares, loaded through the
# API as the mock admin (see dev/seed-dev-data.sh for that token flow), never raw SQL.
# There is none: brief 03g item 2 audited every test and proved each one creates the data
# it needs (categories, posts, media), so the isolated instance starts from a freshly
# migrated, empty database and the suite runs green on it (task 2 report: 126/126 from
# empty, twice in a row and on a fresh start).
#
# Called by start-local.sh --e2e AFTER the instance's API and Web are up and healthy, with
# API_BASE_URL set to that instance's API base URL — so code here may talk to the live API
# (e.g. to upload media through the media endpoint) without the uploads folder being wiped
# underneath it. A non-zero exit fails the start; anything seeded here is gone again on the
# next start (the database is dropped and the uploads folder recreated each time).
#
# If a future test genuinely needs data shared across the whole suite — a category the
# layout tests all need, for example — put it HERE, keep it minimal, and give each test
# that consumes it a fallback that creates the data itself. Data only one test needs
# belongs in that test's fixtures, not here.
exit 0
