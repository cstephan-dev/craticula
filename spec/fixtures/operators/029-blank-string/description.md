Proves AR-6's `blank` operator on a `string` field: SEM-2 pins `blank` as `IS NULL OR = ''` specifically for text fields (unlike non-text types, which are `IS NULL` alone).
