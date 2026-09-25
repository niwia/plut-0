# Rule: Pluto Versioning Scheme

The version number of Pluto must follow this exact format:
`0.0.8-xxday+month+26`

Where:
- `0.0.8` is the base release version.
- `xx` is a 2-digit sequential build counter incrementing by +1 per build (e.g. 01, 02...).
- `day` is the 2-digit day of the build (e.g. 25).
- `month` is the 2-digit month of the build (e.g. 09).
- `26` is the 2-digit year (2026).

Example for build 01 on September 25, 2026:
`0.0.8-0125+09+26`

All places displaying or logging the Pluto version must use this format.
