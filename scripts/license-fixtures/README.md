Synthetic inputs of scripts/test_check_licenses.py: each folder is one case, holding the JSON that `nuget-license -o Json`
prints per scope (`src.json`, `tests.json`, `other.json`) and, for the MPL-2.0 cases, an `adr/` folder. `overrides/` holds
the override files the cases use. Every package here is invented; no real client data.
